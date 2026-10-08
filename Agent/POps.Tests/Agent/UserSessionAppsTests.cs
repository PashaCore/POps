using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Threading;
using POps.Shared;
using POpsAgent;
using Xunit;

namespace POps.Tests.Agent
{
    // 1) Tepsi/watchdog kullanıcı oturumunda başlatılır (saha: kurulum ve güncellemeden sonra tepsi yoktu)
    public class UserSessionAppsTests : TestBase
    {
        // SYSTEM olarak çalışan bir CI'da WTSQueryUserToken başarılı olur ve süreç gerçekten başlardı: o durumda atlanır
        public static bool SkipLaunchTests => WindowsIdentity.GetCurrent().IsSystem;

        [Fact]
        public void NoSignedInUser_StartsNothing() =>
            Assert.Equal((false, false), UserAppsPolicy.WhatToStart(false, false, false, false, true, TimeSpan.FromHours(1)));

        [Fact]
        public void DuringAnUpdate_StartsNothing() =>
            Assert.Equal((false, false), UserAppsPolicy.WhatToStart(true, true, false, false, true, TimeSpan.FromHours(1)));

        [Fact]
        public void MissingApps_AreStarted() =>
            Assert.Equal((true, true), UserAppsPolicy.WhatToStart(true, false, false, false, true, TimeSpan.Zero));

        [Fact]
        public void RunningApps_AreLeftAlone() =>
            Assert.Equal((false, false), UserAppsPolicy.WhatToStart(true, false, true, true, true, TimeSpan.FromHours(1)));

        [Fact]
        public void Tray_WaitsForTheShellButNotForever()
        {
            // Oturum yeni açıldı, görev çubuğu henüz yok: tepsi simgesi kaybolmasın diye beklenir
            Assert.Equal((false, false), UserAppsPolicy.WhatToStart(true, false, true, false, false, TimeSpan.FromSeconds(10)));
            // Özel kabuk (explorer hiç yok): bir dakika sonra yine de başlatılır
            Assert.Equal((false, true), UserAppsPolicy.WhatToStart(true, false, true, false, false, UserAppsPolicy.ShellWait));
        }

        // Testler SYSTEM değildir: kullanıcı belirteci alınamaz, hiçbir şey başlatılmaz ve istisna çıkmaz
        [Fact]
        public void WithoutSystemRights_NothingIsStarted()
        {
            if (SkipLaunchTests) return;
            string harmless = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "whoami.exe");
            uint session = UserSessionLauncher.ActiveConsoleSession();
            Assert.False(UserSessionLauncher.HasSignedInUser(session));
            Assert.False(UserSessionLauncher.TryStart(session, harmless, out int pid, out string error));
            Assert.Equal(0, pid);
            Assert.False(string.IsNullOrEmpty(error));

            Assert.False(UserSessionLauncher.TryStart(session, @"C:\yok\POpsTray.exe", out _, out string missing));
            Assert.Contains("yok", missing);
            Assert.False(UserSessionLauncher.TryStart(UserSessionLauncher.NoSession, harmless, out _, out _));

            new UserSessionApps(AgentHarness.Create("apps-data").Paths, TestEnvironment.NewDir("apps")).EnsureOnce();
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

        // L5 + N1: Windows Installer meşgulken tepsi/watchdog başlatılmaz; ama aynı adlı mutex'i herhangi bir kullanıcı
        // oluşturabildiği için yalnızca sahibi SYSTEM/Administrators olan sayılır (gerçek _MSIExecute'a dokunulmaz)
        [Fact]
        public void OnlyASystemOwnedInstallerMutex_MeansBusy()
        {
            string name = @"Local\POpsTest_MSIExecute_" + Guid.NewGuid().ToString("N");
            Assert.False(UserSessionLauncher.WindowsInstallerBusy(name));
            using (new Mutex(true, name))
            {
                SecurityIdentifier owner = UserSessionLauncher.MutexOwner(name);
                Assert.Equal(WindowsIdentity.GetCurrent().Owner, owner);
                // Yönetici olmayan kullanıcının oluşturduğu mutex tepsiyi durduramaz
                Assert.Equal(PipeOwner.IsTrustedOwner(owner), UserSessionLauncher.WindowsInstallerBusy(name));
            }
        }
    }
}
