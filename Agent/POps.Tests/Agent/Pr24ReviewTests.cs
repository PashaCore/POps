using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using POps.Shared;
using POpsAgent;
using Xunit;

namespace POps.Tests.Agent
{
    // PR #24 incelemesi: yardım masası istek sınırı (M1), talep sahibi (M2), süreç yolu denetimi (M3), ImagePath (L2),
    // sınırlı yanıt okuma (L3), bayat kilit (L4), Windows Installer meşgul (L5)
    public class HelpdeskThrottleTests : TestBase, IDisposable
    {
        private readonly List<string> _tray = new List<string>();
        private int _requests;
        private DateTime _now = new DateTime(2026, 9, 28, 10, 0, 0, DateTimeKind.Utc);

        public HelpdeskThrottleTests()
        {
            AgentUpdate.DataDir = TestEnvironment.NewDir("throttle");
            SecureStore.Dir = TestEnvironment.NewDir("throttle-secure");
            AgentCredentials.SaveSecret("test-secret-0123456789abcdefghijklmn", "HW-A");
        }

        public void Dispose() => AgentUpdate.DataDir = TestEnvironment.DefaultDataDir;

        private Helpdesk Desk(Func<Task<(int?, string)>> response) => new Helpdesk("https://pops.example", () => "HW-A", () => "ogrenci", _tray.Add)
        {
            UtcNow = () => _now,
            Sender = (method, path, payload, what) => { Interlocked.Increment(ref _requests); return response(); },
        };

        private static string Create(string subject) => Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { subject, category = "ag", body = "" })));

        private static JsonElement Last(List<string> tray, string kind)
        {
            string m = tray[^1];
            Assert.StartsWith(kind + ":", m);
            return JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(m.Substring(kind.Length + 1)))).RootElement.Clone();
        }

        [Fact]
        public async Task Create_AtMostEveryTenSeconds()
        {
            Helpdesk desk = Desk(() => Task.FromResult<(int?, string)>((200, "{\"id\":1}")));
            await desk.CreateAsync(Create("Konu bir"));
            await desk.CreateAsync(Create("Konu iki"));
            Assert.Equal(1, _requests);
            Assert.Equal(Helpdesk.BusyMessage, Last(_tray, "TICKET_RESULT").GetProperty("message").GetString());

            _now = _now.AddSeconds(10);
            await desk.CreateAsync(Create("Konu üç"));
            Assert.Equal(2, _requests);
        }

        [Fact]
        public async Task List_OneAtATimeAndAtMostEveryFiveSeconds()
        {
            var gate = new TaskCompletionSource<(int?, string)>();
            Helpdesk desk = Desk(() => gate.Task);

            Task first = desk.ListAsync();
            await desk.ListAsync();                     // ilki sürerken
            Assert.Equal(1, _requests);
            JsonElement busy = Last(_tray, "TICKET_LIST_RESULT");
            Assert.True(busy.GetProperty("busy").GetBoolean());
            Assert.False(busy.GetProperty("ok").GetBoolean());

            gate.SetResult((200, "[]"));
            await first;
            await desk.ListAsync();                     // bittiği saniye: aralık dolmadı
            Assert.Equal(1, _requests);

            _now = _now.AddSeconds(5);
            gate = new TaskCompletionSource<(int?, string)>();
            gate.SetResult((200, "[]"));
            await desk.ListAsync();
            Assert.Equal(2, _requests);
        }

        // Sunucunun cihaz başına 5 sn sınırı (429) hata sayılmaz: tepsi listeyi korur, yalnızca kısa notu gösterir
        [Fact]
        public async Task ServerThrottle_IsQuiet()
        {
            await Desk(() => Task.FromResult<(int?, string)>((429, "{\"detail\":\"Çok sık istek; birkaç saniye sonra tekrar deneyin.\"}"))).ListAsync();
            JsonElement reply = Last(_tray, "TICKET_LIST_RESULT");
            Assert.True(reply.GetProperty("busy").GetBoolean());
            Assert.Equal(Helpdesk.BusyMessage, reply.GetProperty("message").GetString());
        }

        // M1 sertleştirme: tepsi ve watchdog DOTNET_STARTUP_HOOKS ile kullanıcı kodu yüklemez
        [Fact]
        public void UserSessionApps_DisableStartupHooks()
        {
            string root = TestEnvironment.RepoRoot();
            Assert.Contains("<StartupHookSupport>false</StartupHookSupport>", File.ReadAllText(Path.Combine(root, "Agent", "POpsTray", "POpsTray.csproj")));
            Assert.Contains("<StartupHookSupport>false</StartupHookSupport>", File.ReadAllText(Path.Combine(root, "Agent", "POpsWatchdog", "POpsWatchDog", "POpsWatchDog.csproj")));
        }
    }

    public class ProcessIdentityTests : TestBase
    {
        // M2: talebin sahibi isteği yapan sürecin oturumundaki kullanıcı (burada test sürecinin kendisi)
        [Fact]
        public void SessionUserOfAProcess()
        {
            uint session = UserSessionLauncher.SessionOf(Environment.ProcessId);
            Assert.Equal((uint)Process.GetCurrentProcess().SessionId, session);
            if (session == 0) return;   // hizmet oturumundaki CI: kullanıcı adı yok
            Assert.Equal(Environment.UserName, UserSessionLauncher.SessionUser(session), StringComparer.OrdinalIgnoreCase);
            Assert.Null(UserSessionLauncher.SessionUser(UserSessionLauncher.NoSession));
        }

        // M3: aynı ADI taşıyan ama başka yerdeki program "çalışıyor" sayılmaz
        [Fact]
        public void ProcessIsMatchedByPathNotByName()
        {
            string self = Environment.ProcessPath;
            Assert.True(UserSessionLauncher.IsRunning(self));
            string impostor = Path.Combine(TestEnvironment.NewDir("indirilenler"), Path.GetFileName(self));
            Assert.False(UserSessionLauncher.IsRunning(impostor));
            Assert.Equal(self, UserSessionLauncher.ImagePath(Environment.ProcessId), StringComparer.OrdinalIgnoreCase);
        }

        // L2: tırnaksız ve boşluklu ImagePath "C:\Program" vermez
        [Theory]
        [InlineData("\"C:\\Program Files\\POps\\POpsAgent.exe\" --service", "C:\\Program Files\\POps\\POpsAgent.exe")]
        [InlineData("C:\\Program Files\\POps\\POpsAgent.exe", "C:\\Program Files\\POps\\POpsAgent.exe")]
        [InlineData("C:\\Program Files\\POps\\POpsAgent.EXE -x", "C:\\Program Files\\POps\\POpsAgent.EXE")]
        [InlineData("  C:\\POps\\POpsAgent.exe  ", "C:\\POps\\POpsAgent.exe")]
        [InlineData(null, null)]
        [InlineData("", null)]
        public void ServiceImagePath_IsParsed(string imagePath, string expected) =>
            Assert.Equal(expected, ServiceImagePath.ExecutablePath(imagePath));

        // L5: Windows Installer bir işlem yürütürken tepsi/watchdog başlatılmaz (gerçek _MSIExecute'a dokunulmaz)
        [Fact]
        public void WindowsInstallerMutex_MeansBusy()
        {
            string name = @"Local\POpsTest_MSIExecute_" + Guid.NewGuid().ToString("N");
            Assert.False(UserSessionLauncher.WindowsInstallerBusy(name));
            using (new Mutex(true, name))
                Assert.True(UserSessionLauncher.WindowsInstallerBusy(name));
        }
    }

    public class BoundedResponseTests : TestBase
    {
        // Parçalı (chunked) yanıtta Content-Length yoktur: akış sınırlı okunur
        private sealed class NoLengthStream : MemoryStream
        {
            public NoLengthStream(byte[] data) : base(data) { }
            public override bool CanSeek => false;
        }

        [Fact]
        public async Task ResponseWithoutLength_IsCapped()
        {
            byte[] big = Encoding.UTF8.GetBytes(new string('x', 5000));
            Assert.Null(await AgentHttp.ReadLimitedAsync(new StreamContent(new NoLengthStream(big)), 1024));
            Assert.Equal(5000, (await AgentHttp.ReadLimitedAsync(new StreamContent(new NoLengthStream(big)), 10000)).Length);
            Assert.Null(await AgentHttp.ReadLimitedAsync(new ByteArrayContent(big), 1024));
        }
    }

    public class StaleLockDrillTests : TestBase, IDisposable
    {
        public StaleLockDrillTests()
        {
            AgentUpdate.DataDir = TestEnvironment.NewDir("stale");
            Directory.CreateDirectory(AgentUpdate.SecureDataDir);
        }

        public void Dispose()
        {
            AgentUpdate.DataDir = TestEnvironment.DefaultDataDir;
            AgentUpdate.InstalledVersionOverride = null;
        }

        // L4: çökmüş bir updater'dan kalan eski kilit "güncelleme var" sayılmaz; işaret tüketilmez
        [Fact]
        public void StaleUpdateLock_DoesNotConsumeTheMarker()
        {
            File.WriteAllText(AgentUpdate.RollbackDrillPath, "");
            File.WriteAllText(AgentUpdate.LockPath, JsonSerializer.Serialize(new { from_version = "0.1.5-alpha", to_version = "0.1.6-alpha", started_at = 1000 }));
            File.SetLastWriteTimeUtc(AgentUpdate.LockPath, DateTime.UtcNow.AddMinutes(-20));
            AgentUpdate.InstalledVersionOverride = "0.1.6-alpha";

            Assert.False(AgentUpdate.ApplyRollbackDrillOnStartup());
            Assert.True(AgentUpdate.RollbackDrillRequested());
        }
    }
}
