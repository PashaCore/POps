#if !NETFRAMEWORK
using System;
using System.Collections.Generic;
using System.IO;
using POpsAgent;

namespace POps.Tests
{
    // Testin ajanı: kendi geçici klasörleri (TestEnvironment.Root altında; gerçek C:\POpsData, C:\POpsLogs ve C:\POps'a asla
    // dokunulmaz) ve onlarla kurulan AgentContext. Statiklere dokunmaz: yalnızca bunu kullanan sınıf paralel çalışabilir.
    //
    //     using var agent = AgentHarness.Create();
    //     var spool = new ResultSpool(agent.Paths.SecureFile(ResultSpool.FileName));
    //     using var worker = new Worker(NullLogger<Worker>.Instance, agent.Context);
    //
    // Worker bölmesinin (b) adımları bağlama parça ekledikçe (b2: çalışma durumu, b3: makine kapıları, b4: DnsPolicyMonitor)
    // harness de onların test hâllerini kurar ve test sınıfları bir bir buraya geçer (bkz. docs/design/worker-split.md).
    internal sealed class AgentHarness : IDisposable
    {
        private readonly string _root;

        private AgentHarness(string root, AgentPaths paths)
        {
            _root = root;
            Paths = paths;
            Context = new AgentContext(paths);
        }

        public AgentPaths Paths { get; }
        public AgentContext Context { get; }

        // Testin kendi klasörleri: veri, güvenli depo (verinin içinde, serviste olduğu gibi) ve log. Ayar dosyası yok
        // (gerçek C:\POps\appsettings.json okunmaz). Klasörler oluşturulur: gerçek kurulumda da hep vardır.
        public static AgentHarness Create(string name = "agent")
        {
            string root = TestEnvironment.NewDir(name);
            string data = Path.Combine(root, "data");
            AgentPaths paths = AgentPaths.ForFolders(data, Path.Combine(root, "logs"), Array.Empty<string>());
            Directory.CreateDirectory(paths.SecureDir);
            Directory.CreateDirectory(paths.LogDir);
            return new AgentHarness(root, paths);
        }

        // Worker bölmesinin henüz kaldırmadığı statiklerin gösterdiği klasörler (SecureStore.Dir, AgentUpdate.DataDir,
        // ServerTrust.CaPath, POpsHelpers.ConfigPaths ve süreç geneli POpsHelpers.MachineLogDir). Bu statikleri hâlâ kuran
        // SharedState sınıfları içindir: Worker ve onun kurduğu servisler, statikleri okuyan b2/b3 koduyla aynı klasörleri
        // görür. Bir sınıf serbest kalınca Create()'e geçer. Klasörler testindir; harness silmez.
        public static AgentHarness FromStatics() =>
            new AgentHarness(null, new AgentPaths(AgentUpdate.DataDir, SecureStore.Dir, POps.Shared.POpsHelpers.MachineLogDir,
                new List<string>(POps.Shared.POpsHelpers.ConfigPaths), POps.Shared.ServerTrust.CaPath));

        // Create()'in klasörleri silinir (korumalı dosyaların ACL'inde "servis hesabı" test kullanıcısıdır)
        public void Dispose()
        {
            if (_root == null) return;
            try { Directory.Delete(_root, true); } catch { }
        }
    }
}
#endif
