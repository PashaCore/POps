#if !NETFRAMEWORK
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using POps.Shared;
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
    // Worker bölmesinin (b) adımları bağlama parça ekledikçe (b2: çalışma durumu, b4: DnsPolicyMonitor, b3: makine kapıları)
    // harness de onların test hâllerini kurar ve test sınıfları bir bir buraya geçer (bkz. docs/design/worker-split.md).
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

        // Testin kendi klasörleri: veri, güvenli depo (verinin içinde, serviste olduğu gibi) ve log. Ayar dosyası yok
        // (gerçek C:\POps\appsettings.json okunmaz). Klasörler oluşturulur: gerçek kurulumda da hep vardır. Çalışma durumu
        // servisteki gibi kurulur (bkz. Program), her parçanın yeni bir örneği; http: sunucu isteklerinin sahte işleyicisi
        // (null: servisteki ServerTrust işleyicisi).
        public static AgentHarness Create(string name = "agent", HttpMessageHandler http = null)
        {
            string root = TestEnvironment.NewDir(name);
            string data = Path.Combine(root, "data");
            AgentPaths paths = AgentPaths.ForFolders(data, Path.Combine(root, "logs"), Array.Empty<string>());
            Directory.CreateDirectory(paths.SecureDir);
            Directory.CreateDirectory(paths.LogDir);
            return new AgentHarness(root, NewContext(paths, http));
        }

        // Verilen klasörlerle (ör. AgentDirectories.Apply'ın seçtikleri) testin kendi çalışma durumu. Klasörler testindir;
        // harness silmez.
        public static AgentHarness ForPaths(AgentPaths paths, HttpMessageHandler http = null) => new AgentHarness(null, NewContext(paths, http));

        private static AgentContext NewContext(AgentPaths paths, HttpMessageHandler http)
        {
            var trust = new ServerTrust(paths.ServerCaPath);
            var credentials = new CredentialStore(paths);
            return new AgentContext(paths,
                new CapabilityState(paths.SecureFile(AgentCapabilities.FileName), trust),
                new ModuleState(),
                credentials,
                trust,
                new AgentHttp(http ?? trust.NewHandler(), credentials),
                new AgentUpdater(paths));
        }

        // Worker bölmesinin henüz kaldırmadığı statiklerin gösterdiği klasörler (SecureStore.Dir, AgentUpdate.DataDir,
        // POpsHelpers.ConfigPaths ve süreç geneli POpsHelpers.MachineLogDir) ve statik kalan kodun okuduğu süreç
        // varsayılanları (AgentCapabilities.Process, AgentCredentials.Process, AgentHttp.Default). Bu statikleri hâlâ kuran
        // SharedState sınıfları içindir: Worker ve onun kurduğu servisler, statikleri okuyan b3/b4 koduyla aynı klasörleri ve
        // durumu görür. Sunucu modülleri ve güncelleme durumu her çağrıda yenidir. Bir sınıf serbest kalınca Create()'e geçer.
        // Klasörler testindir; harness silmez.
        public static AgentHarness FromStatics()
        {
            var paths = new AgentPaths(AgentUpdate.DataDir, SecureStore.Dir, POpsHelpers.MachineLogDir,
                new List<string>(POpsHelpers.ConfigPaths), SecureStore.PathOf(ServerTrust.FileName));
            return new AgentHarness(null, new AgentContext(paths, AgentCapabilities.Process, new ModuleState(), AgentCredentials.Process,
                new ServerTrust(paths.ServerCaPath), AgentHttp.Default, new AgentUpdater(paths)));
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
