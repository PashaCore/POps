using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using POps.Shared;
using POpsAgent;
using Xunit;

namespace POps.Tests.Agent
{
    // Makine kapıları güvenli varsayılanla (bkz. MachineSeams): testten ulaşılan hiçbir yol gerçek güvenlik duvarı betiğini,
    // kiosk kayıt defterini, sistem ortam değişkenlerini, BITS'i, güç API'sini, winget'i, süreç kapatmayı, updater'ı, servis
    // durdurmayı, kurulu servisin tepsi borusunu ya da dinleyen eş önbelleği sunucusunu kuramaz. Gerçek kapılar yalnızca
    // MachineSeams.Production'da kurulur ve onu yalnızca Program çağırır; kapı verilmeyen her yol (MachineSeams'in parametresiz
    // hâli, AgentHarness) reddeden sahtelere düşer.
    public class MachineSeamsTests : TestBase
    {
        internal static MethodInfo Production =>
            typeof(MachineSeams).GetMethod(nameof(MachineSeams.Production), BindingFlags.Static | BindingFlags.NonPublic);

        // Production'ın kurduğu gerçek kapılar: doğrudan çağırdığı, kurduğu ya da temsilcisini aldığı yöntemler (MachineSeams'in
        // kendi kurucusu ve init erişimcileri ile temsilci türlerinin kurucuları hariç; lambda'lar dahil)
        internal static List<MethodBase> ProductionTargets() =>
            StaticAccessScanner.DirectCallees(Production)
                .Where(m => m.DeclaringType != typeof(MachineSeams) && !(m.IsConstructor && typeof(Delegate).IsAssignableFrom(m.DeclaringType)))
                .Distinct().ToList();

        // Her kapı reddeden sahtedir: temsilciler MachineSeams.Refusing'te (yeni bir kapı da varsayılanını orada almalı), BITS
        // yok, kiosk kaydı bellekte, tepsi borusunun adı kurulu servisinkinden farklı ve bağlanan istemciyi reddeder
        internal static void AssertRefusing(MachineSeams machine)
        {
            foreach (PropertyInfo property in typeof(MachineSeams).GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                object value = property.GetValue(machine);
                if (property.Name == nameof(MachineSeams.Bits)) Assert.Null(value);
                else if (value is Delegate seam)
                {
                    Assert.True(seam.Method.DeclaringType == typeof(MachineSeams.Refusing),
                        $"{property.Name}: {seam.Method.DeclaringType}.{seam.Method.Name} reddeden sahte değil");
                    if (property.Name == nameof(MachineSeams.PeerCacheServer))
                    {
                        // Eş önbelleğinin sunucusu kurulur ama dinlemez: Start, portu açılamayan bir sunucu gibi reddeder
                        using IPeerCacheServer server = machine.PeerCacheServer(new string('0', 64), "yok", _ => true, PeerCache.MaxClients);
                        Assert.Throws<SocketException>(server.Start);
                        Assert.Equal(0, server.Port);
                    }
                    // Bu bilgisayarın sistem ortam değişkenleri okunmaz (eski sürümden kalan gizli değer yok sayılır)
                    if (property.Name == nameof(MachineSeams.ReadMachineVariable)) Assert.Null(machine.ReadMachineVariable("POPS_BYPASS_SECRET"));
                }
                else if (property.Name == nameof(MachineSeams.Kiosk))
                    Assert.False(value is WindowsKioskRegistry, "kiosk kaydı gerçek kayıt defteri");
                else if (property.Name == nameof(MachineSeams.TrayPipe))
                {
                    var pipe = (TrayPipeOptions)value;
                    Assert.StartsWith(TrayPipeOptions.InstalledName + "-", pipe.Name);
                    Assert.NotNull(pipe.ClientCheck(null, out uint pid));
                    Assert.Equal(0u, pid);
                }
                else Assert.Fail($"{property.Name}: bu testin bilmediği bir kapı; reddeden varsayılanını burada denetleyin");
            }
        }

        [Fact]
        public void Defaults_AreTheRefusingFakes() => AssertRefusing(new MachineSeams());

        [Fact]
        public void Harness_GivesTheRefusingFakes()
        {
            using var agent = AgentHarness.Create("seams");
            AssertRefusing(agent.Machine);
            Assert.IsType<FakeKioskRegistry>(agent.Machine.Kiosk);
            Assert.Same(agent.Machine, agent.Context.Machine);
        }

        // Production'a ve kurduğu gerçek kapılara test derlemesinin hiçbir türünden (testler, harness, sahteler) ulaşılmaz;
        // testler onu yalnızca adıyla anar. Program ulaşır: tarama gerçekten bir şey görüyor.
        [Fact]
        public void Production_IsReachableOnlyFromProgram()
        {
            List<MethodBase> targets = ProductionTargets();
            Assert.Contains(targets, m => m.DeclaringType == typeof(WindowsKioskRegistry) && m.IsConstructor);
            Assert.Contains(targets, m => m.DeclaringType == typeof(NetworkIsolation) && m.Name == nameof(NetworkIsolation.RunPowerShellAsync));
            Assert.Contains(targets, m => m.DeclaringType == typeof(AgentUpdater) && m.Name == nameof(AgentUpdater.StartUpdater));
            targets.Add(Production);

            var reaching = typeof(MachineSeamsTests).Assembly.GetTypes()
                .Where(t => t.DeclaringType == null)
                .Select(t => (t.FullName, Path: StaticAccessScanner.PathToAny(t, targets)))
                .Where(x => x.Path != null)
                .Select(x => $"  {x.FullName}: {x.Path}")
                .ToList();
            Assert.True(reaching.Count == 0, "Test derlemesinden gerçek makine kapısına ulaşılıyor:" + Environment.NewLine + string.Join(Environment.NewLine, reaching));
            Assert.NotNull(StaticAccessScanner.PathToAny(typeof(Program), new[] { Production }));
        }
    }

    // Harness'le kurulan Worker ve servisler yalnızca bağlamın reddeden kapılarını tutar: nesne çizgesinde gerçek bir kapının
    // (Production'ın kurduğu temsilci ya da gerçek kayıt defteri) izi yoktur, parçalar kapılarını bağlamdan alır.
    public class MachineSeamsWorkerTests : TestBase
    {
        private static bool Ours(Type type) =>
            type.Assembly == typeof(Worker).Assembly || type.Assembly == typeof(IKioskRegistry).Assembly || type.Assembly == typeof(MachineSeamsWorkerTests).Assembly
            || (type.IsValueType && type.Namespace == "System.Collections.Generic");   // KeyValuePair (sözlük öğeleri)

        // Nesneden ulaşılan her nesne: ajanın, POps.Shared'ın ve testlerin türlerinin alanları, temsilcilerin hedefleri ve
        // koleksiyon öğeleri (yalnızca alanlar okunur; özellik ya da yöntem çağrılmaz)
        private static List<object> Graph(object root)
        {
            var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
            var stack = new Stack<object>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                object current = stack.Pop();
                if (current == null || !seen.Add(current)) continue;
                Type type = current.GetType();
                if (current is Delegate seam)
                {
                    foreach (Delegate each in seam.GetInvocationList())
                    {
                        stack.Push(each);
                        stack.Push(each.Target);
                    }
                    continue;
                }
                if (current is string || type.IsPrimitive || type.IsEnum) continue;
                if (current is IEnumerable items && (type.IsArray || (type.Namespace?.StartsWith("System.Collections", StringComparison.Ordinal) ?? false)))
                {
                    foreach (object item in items) stack.Push(item);
                    continue;
                }
                if (!Ours(type)) continue;
                for (Type t = type; t != null && t != typeof(object); t = t.BaseType)
                    foreach (FieldInfo field in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                        if (!field.FieldType.IsPrimitive && !field.FieldType.IsEnum) stack.Push(field.GetValue(current));
            }
            return seen.ToList();
        }

        // Nesnenin kendi alanlarının değerleri (bir düzey)
        private static List<object> Fields(object part) =>
            part.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Select(f => f.GetValue(part)).ToList();

        private static (Guid, int) Key(MethodBase method) => (method.Module.ModuleVersionId, method.MetadataToken);

        private static void AssertNoProductionSeam(object root)
        {
            var production = new HashSet<(Guid, int)>(MachineSeamsTests.ProductionTargets().Select(Key));
            List<object> graph = Graph(root);
            var found = graph.OfType<Delegate>().SelectMany(d => d.GetInvocationList()).Where(d => production.Contains(Key(d.Method)))
                .Select(d => $"{d.Method.DeclaringType}.{d.Method.Name}").Distinct().ToList();
            Assert.True(found.Count == 0, "Gerçek makine kapısı: " + string.Join(", ", found));
            Assert.DoesNotContain(graph, o => o is WindowsKioskRegistry);
            Assert.All(graph.OfType<TrayPipeOptions>(), pipe => Assert.NotEqual(TrayPipeOptions.InstalledName, pipe.Name));
            Assert.Contains(graph, o => o is Delegate);   // çizge gerçekten gezildi
        }

        [Fact]
        public void Worker_AndThePartsItBuilds_HoldOnlyTheHarnessSeams()
        {
            using var agent = AgentHarness.Create("seams-worker");
            using var worker = new Worker(NullLogger<Worker>.Instance, agent.Context);
            MachineSeams machine = agent.Machine;
            MachineSeamsTests.AssertRefusing(machine);
            AssertNoProductionSeam(worker);

            // Her parça kapısını bağlamdan alır (kendi varsayılanı yok)
            Assert.Same(machine.FirewallScripts, worker.Isolation.Scripts);
            Assert.Same(machine.Kiosk, worker.Quarantine.Kiosk.Registry);
            Assert.Same(machine.FirewallScripts, worker.Exam.Scripts);
            Assert.Same(machine.ExamResolver, worker.Exam.Resolver);
            Assert.Same(machine.StopProcess, worker.Exam.StopProcess);
            Assert.Same(agent.Context.Clock, worker.Exam.Clock);
            Assert.Contains(machine.Power, Fields(worker.Power));
            Assert.Contains(machine.HasConsoleUser, Fields(worker.Power));
            Assert.Contains(machine.HasConsoleUser, Fields(worker.Messages));
            Assert.Contains(machine.WingetLocator, Fields(worker.Dispatcher.HandlerFor(WingetInstall.Action)));
            FileTransfer transfer = Assert.IsType<FileTransferHandler>(worker.Dispatcher.HandlerFor("file_push")).Transfer;
            Assert.Contains(machine.PublicDesktop, Fields(transfer));
            Assert.Same(machine.Profiles, transfer.ProfileInfo);
            PeerCache cache = agent.Updater.PeerCache;
            Assert.Same(machine.PeerCacheFirewall, cache.FirewallRunner);
            Assert.Same(machine.PeerCacheServer, cache.NewServer);
            Assert.Contains(machine.ReadMachineVariable, Fields(agent.Credentials));
            Assert.Contains(machine.DeleteMachineVariable, Fields(agent.Credentials));
            Assert.Null(agent.Updater.Bits.Runner);
            Assert.False(agent.Updater.Bits.Enabled);
            Assert.Same(machine.LaunchUpdater, agent.Updater.LaunchUpdater);
        }

        [Fact]
        public void Services_BuiltWithTheHarness_HoldNoProductionSeam()
        {
            using var agent = AgentHarness.Create("seams-services");
            MachineSeams machine = agent.Machine;
            AssertNoProductionSeam(agent.Context);
            AssertNoProductionSeam(agent.NewUpdater("0.1.0", launch: (_, _, _) => false));
            AssertNoProductionSeam(new NetworkIsolation(agent.Paths.SecureFile(NetworkIsolation.StateFileName), machine.FirewallScripts));
            AssertNoProductionSeam(new ExamMode(agent.Paths.SecureFile(ExamMode.StateFileName), machine.FirewallScripts, machine.ExamResolver, machine.StopProcess,
                agent.Context.Clock));
            AssertNoProductionSeam(new QuarantineControl(agent.Paths, _ => { }, () => Task.FromResult(true), () => Task.FromResult(true), machine.Kiosk));
            AssertNoProductionSeam(new FileTransfer(agent.Paths, agent.Http, agent.Credentials, machine.PublicDesktop, machine.Profiles));
            var outbox = new ResultOutbox(new ServerHandshake(), new ResultSpool(agent.Paths.SecureFile(ResultSpool.FileName)), _ => Task.FromResult(true));
            AssertNoProductionSeam(new PowerActions(new TaskResults(outbox, () => null), _ => Task.CompletedTask, _ => { }, () => false, agent.Capabilities,
                machine.Power, machine.HasConsoleUser));
            AssertNoProductionSeam(new UserMessages((_, _, _) => Task.CompletedTask, _ => Task.CompletedTask, _ => { }, () => false, agent.Capabilities,
                machine.HasConsoleUser));
            using var pipe = new TrayPipeServer(machine.TrayPipe);   // Start çağrılmaz: boru açılmaz
            AssertNoProductionSeam(pipe);
        }
    }
}
