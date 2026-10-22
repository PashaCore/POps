#if !NETFRAMEWORK
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using Microsoft.Win32.SafeHandles;
using POps.Shared;
using POpsAgent;

namespace POps.Tests
{
    // Testin ajanı: kendi geçici klasörleri (TestEnvironment.Root altında; gerçek C:\POpsData, C:\POpsLogs, C:\POps ve %TEMP%'e
    // asla dokunulmaz), kendi çalışma durumu ve makineye dokunmayan kapılarla kurulan AgentContext. Statiklere dokunmaz:
    // yalnızca bunu kullanan sınıf paralel çalışabilir.
    //
    //     using var agent = AgentHarness.Create();
    //     var spool = new ResultSpool(agent.Paths.SecureFile(ResultSpool.FileName));
    //     using var worker = new Worker(NullLogger<Worker>.Instance, agent.Context);
    //
    // Makine kapıları (bkz. MachineSeams): verilmezse reddeden sahteler (MachineSeams'in varsayılanı; kiosk kaydı bellekteki
    // FakeKioskRegistry). Gerçek güvenlik duvarına, kayıt defterine, sistem ortam değişkenlerine, BITS'e, güç API'sine, winget'e,
    // süreçlere, updater'a ve kurulu servisin tepsi borusuna hiçbir yoldan ulaşılmaz, eş önbelleği dinlemez (bkz.
    // MachineSeamsTests). Bir kapıyı sınayan test yalnızca onun sahtesini verir: AgentHarness.Create(machine: new MachineSeams
    // { FirewallScripts = ... }).
    internal sealed class AgentHarness : IDisposable
    {
        private readonly string _root;

        private AgentHarness(string root, AgentContext context)
        {
            _root = root;
            Context = context;
        }

        public AgentContext Context { get; }
        public AgentPaths Paths => Context.Paths;
        public CapabilityState Capabilities => Context.Capabilities;
        public ModuleState Modules => Context.Modules;
        public CredentialStore Credentials => Context.Credentials;
        public ServerTrust Trust => Context.Trust;
        public AgentHttp Http => Context.Http;
        public AgentUpdater Updater => Context.Updater;
        public MachineSeams Machine => Context.Machine;
        // Bellekteki kiosk kaydı (testin verdiği başka bir sahteyse null)
        public FakeKioskRegistry Kiosk => Machine.Kiosk as FakeKioskRegistry;

        // DNS politika izleme (bkz. DnsPolicyMonitor; serviste her Worker'ın kendi örneği, Worker.Dns), bu bağlamın HTTP istemcisi
        // ve kimlik bilgileriyle. DNS önbelleği testin sahtesidir (gerçek önbellek okunmaz); verilmeyen karantina, hata ve ihlal
        // bildirimi hiçbir şey yapmaz (sunucuya gidilmez), saat verilmezse gerçek saattir.
        public DnsPolicyMonitor NewDnsMonitor(Func<IEnumerable<string>> cache, Action<string, string> reporter = null,
            Action<string> quarantine = null, Action<string> errors = null, TimeProvider clock = null) =>
            new DnsPolicyMonitor(quarantine ?? (_ => { }), errors ?? (_ => { }), Http, Credentials, clock ?? TimeProvider.System, cache,
                reporter ?? ((_, _) => { }));

        // Bu bağlamın güncelleme durumu, testin sürümü, anahtarı ve başlatıcısıyla (bkz. AgentUpdater; eş önbelleği, BITS ve
        // updater bu bağlamın makine kapılarından)
        public AgentUpdater NewUpdater(string installedVersion = null, string trustedKey = null, Func<string, string, string, bool> launch = null) =>
            new AgentUpdater(Paths, Capabilities, Machine, installedVersion, trustedKey, launch);

        // Testin kendi klasörleri: veri, güvenli depo (verinin içinde, serviste olduğu gibi), log ve görev dosyaları. Ayar dosyası
        // yok (gerçek C:\POps\appsettings.json okunmaz). Klasörler oluşturulur: gerçek kurulumda da hep vardır. Çalışma durumu
        // servisteki gibi kurulur (bkz. Program), her parçanın yeni bir örneği; http: sunucu isteklerinin sahte işleyicisi
        // (null: servisteki ServerTrust işleyicisi); configPaths: ayar dosyaları (null: yok); machine: makine kapıları (null:
        // reddeden sahteler); clock: bilgisayarın saati ve monotonik saat (null: servisteki gibi gerçek saat, AgentClock).
        public static AgentHarness Create(string name = "agent", HttpMessageHandler http = null, MachineSeams machine = null,
            IReadOnlyList<string> configPaths = null, TimeProvider clock = null)
        {
            string root = TestEnvironment.NewDir(name);
            string data = Path.Combine(root, "data");
            string secure = Path.Combine(data, "secure");
            var paths = new AgentPaths(data, secure, Path.Combine(root, "logs"), configPaths ?? Array.Empty<string>(),
                Path.Combine(secure, ServerTrust.FileName), taskDir: Path.Combine(root, "tasks"));
            Directory.CreateDirectory(paths.SecureDir);
            Directory.CreateDirectory(paths.LogDir);
            Directory.CreateDirectory(paths.TaskDir);
            return new AgentHarness(root, NewContext(paths, http, machine, clock));
        }

        // Verilen klasörlerle (ör. AgentDirectories.Apply'ın seçtikleri) testin kendi çalışma durumu ve makine kapıları.
        // Klasörler testindir; harness silmez.
        public static AgentHarness ForPaths(AgentPaths paths, HttpMessageHandler http = null, MachineSeams machine = null) =>
            new AgentHarness(null, NewContext(paths, http, machine, null));

        // Testlerin makine kapıları: MachineSeams'in reddeden varsayılanları, kiosk kaydı bellekteki FakeKioskRegistry
        public static MachineSeams RefusingMachine() => new MachineSeams { Kiosk = new FakeKioskRegistry() };

        // Sahte tepsinin bağlandığı boru: kurulu servisle çakışmayan ad, bağlanan istemci denetlenmeden kabul edilir
        public static TrayPipeOptions TestTrayPipe() =>
            new TrayPipeOptions("POpsTrayPipe-test-" + Guid.NewGuid().ToString("N").Substring(0, 8), AcceptAnyClient);

        private static string AcceptAnyClient(SafePipeHandle pipe, out uint clientPid)
        {
            clientPid = 0;
            return null;
        }

        private static AgentContext NewContext(AgentPaths paths, HttpMessageHandler http, MachineSeams machine, TimeProvider clock)
        {
            machine ??= RefusingMachine();
            var trust = new ServerTrust(paths.ServerCaPath);
            var credentials = new CredentialStore(paths, machine.ReadMachineVariable, machine.DeleteMachineVariable);
            var capabilities = new CapabilityState(paths.SecureFile(AgentCapabilities.FileName), trust);
            return new AgentContext(paths,
                capabilities,
                new ModuleState(),
                credentials,
                trust,
                new AgentHttp(http ?? trust.NewHandler(), credentials),
                new AgentUpdater(paths, capabilities, machine),
                machine,
                clock ?? AgentClock.Instance);
        }

        // Create()'in klasörleri silinir (korumalı dosyaların ACL'inde "servis hesabı" test kullanıcısıdır)
        public void Dispose()
        {
            if (_root == null) return;
            try { Directory.Delete(_root, true); } catch { }
        }
    }
}
#endif
