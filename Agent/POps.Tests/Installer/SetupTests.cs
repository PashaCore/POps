using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Web.Script.Serialization;
using POps.Installer;
using Xunit;

namespace POps.Tests.Installer
{
    // MSI custom action'larının mantığı (Installer/agent/CustomActions). Kurulumun dokunduğu bütün klasörler
    // geçici klasörlere yönlendirilir: gerçek C:\POpsData, C:\POpsLogs, C:\POps ve Program Files'a dokunulmaz.
    public class SetupTests : TestBase
    {
        private readonly string _root = TestEnvironment.NewDir("msi");
        private readonly List<string> _log = new List<string>();

        // Üst klasör zinciri geçici test klasöründe durur (bkz. Setup.TrustedBaseForTests)
        public SetupTests() => Setup.TrustedBaseForTests = _root;

        private Layout NewLayout() => new Layout
        {
            DataDir = Path.Combine(_root, "POpsData"),
            SecureDir = Path.Combine(_root, "POpsData", "secure"),
            LogDir = Path.Combine(_root, "POpsLogs"),
            LegacyDirs = new[] { Path.Combine(_root, "C_POps"), Path.Combine(_root, "PF86", "POps"), Path.Combine(_root, "PF", "POps") },
        };

        private string InstallDir => Path.Combine(_root, "PF", "POps");

        private string Configure(Layout layout, params (string Key, string Value)[] properties)
        {
            var data = new Dictionary<string, string>
            {
                ["INSTALLFOLDER"] = InstallDir + "\\",
                ["INSTALLDIR_STATE"] = Directory.Exists(InstallDir) ? Setup.StatePops : Setup.StateNew,
            };
            foreach (var (key, value) in properties) data[key] = value;
            return Setup.Configure(data, layout, _log.Add);
        }

        private static void Write(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            if (File.Exists(path)) File.Delete(path);
            File.WriteAllText(path, text);
        }

        private static Dictionary<string, object> Json(string path) => (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(File.ReadAllText(path));

        private string Secure(Layout layout, string name) => Path.Combine(layout.SecureDir, name);

        private static bool LockedDown(FileSystemSecurity sec) =>
            sec.AreAccessRulesProtected && sec.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().All(r =>
                r.IdentityReference.Equals(Setup.SystemSid) || r.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)));

        [Fact]
        public void LegacyInstall_ServerUrlFromCPopsFirst_BypassSecretMoved()
        {
            Layout layout = NewLayout();
            Write(Path.Combine(layout.LegacyDirs[0], "appsettings.json"), "{\"ServerUrl\":\"https://live.example\"}");
            Write(Path.Combine(layout.LegacyDirs[1], "appsettings.json"), "{\"ServerUrl\":\"https://stale.example\",\"BypassSecret\":\"legacy-bp\"}");

            Assert.Null(Configure(layout, ("ENROLL_TOKEN", "AbCdEfGhIjKlMnOpQrStUvWxYz012345")));

            var config = Json(Path.Combine(InstallDir, "appsettings.json"));
            Assert.Equal("https://live.example", config["ServerUrl"]);
            Assert.False(config.ContainsKey("BypassSecret"));
            Assert.Equal("legacy-bp", File.ReadAllText(Secure(layout, "bypass.secret")));
            Assert.Equal("AbCdEfGhIjKlMnOpQrStUvWxYz012345", File.ReadAllText(Secure(layout, "enroll.token")));
            Assert.True(LockedDown(File.GetAccessControl(Path.Combine(InstallDir, "appsettings.json"))));
            Assert.True(LockedDown(File.GetAccessControl(Secure(layout, "bypass.secret"))));
            Assert.DoesNotContain(_log, l => l.Contains("legacy-bp") || l.Contains("AbCdEfGh"));
        }

        [Fact]
        public void DataAndLogDirectories_AreLockedDown()
        {
            Layout layout = NewLayout();
            Assert.Null(Configure(layout, ("SERVER_URL", "https://pops.example")));
            DirectorySecurity data = Directory.GetAccessControl(layout.DataDir);
            var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
            Assert.True(data.AreAccessRulesProtected);
            Assert.Contains(data.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(), r => r.IdentityReference.Equals(users) && (r.FileSystemRights & FileSystemRights.WriteData) == 0);
            Assert.True(LockedDown(Directory.GetAccessControl(layout.SecureDir)));
            Assert.True(LockedDown(Directory.GetAccessControl(layout.LogDir)));
        }

        // Kullanıcının yazabildiği bir klasöre kurulum (ör. INSTALLFOLDER=C:\POps) SYSTEM olarak kod çalıştırmaya
        // açılırdı: kurulum klasörünün izinleri daraltılır
        [Fact]
        public void InstallFolder_IsLockedDown_EvenWhenUsersCouldWrite()
        {
            Directory.CreateDirectory(InstallDir);
            DirectorySecurity open = Directory.GetAccessControl(InstallDir);
            open.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), FileSystemRights.Modify,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            Directory.SetAccessControl(InstallDir, open);
            Assert.True(Setup.UsersCanWrite(Directory.GetAccessControl(InstallDir)));

            // Klasör boştu (kurulumdan önce yalnızca POps'unkiler): daraltılır
            Assert.Null(Configure(NewLayout(), ("SERVER_URL", "https://pops.example"), ("INSTALLDIR_STATE", Setup.StatePops)));

            DirectorySecurity sec = Directory.GetAccessControl(InstallDir);
            Assert.True(sec.AreAccessRulesProtected);
            Assert.False(Setup.UsersCanWrite(sec));
            var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
            Assert.Contains(sec.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(),
                r => r.IdentityReference.Equals(users) && (r.FileSystemRights & FileSystemRights.ExecuteFile) != 0);
        }

        // ---------------------------------------------------------------- INSTALLFOLDER denetimi (immediate)

        private static readonly ISet<string> NoKnownFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static void GrantAuthenticatedUsers(string dir, FileSystemRights rights, PropagationFlags propagation = PropagationFlags.None)
        {
            DirectorySecurity sec = Directory.GetAccessControl(dir);
            sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), rights,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, propagation, AccessControlType.Allow));
            Directory.SetAccessControl(dir, sec);
        }

        [Theory]
        [InlineData(@"C:\", "sürücü kökü")]
        [InlineData(@"C:", "tam bir yerel yol")]
        [InlineData(@"\\server\share\POps", "ağ yolu")]
        [InlineData(@"//server/share/POps", "ağ yolu")]
        [InlineData(@"POps", "tam bir yerel yol")]
        [InlineData("", "boş")]
        public void InvalidInstallFolders_AreRejected(string dir, string reason)
        {
            string error = Setup.CheckInstallFolder(dir, Setup.DefaultProtectedFolders(), NoKnownFiles, out string state);
            Assert.NotNull(error);
            Assert.Contains(reason, error);
            Assert.Null(state);
        }

        [Fact]
        public void SystemFoldersAndTheirParents_AreRejected()
        {
            IList<string> system = Setup.DefaultProtectedFolders();
            foreach (string dir in new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:") + "\\Users",
            })
            {
                string error = Setup.CheckInstallFolder(dir, system, NoKnownFiles, out _);
                Assert.True(error != null && error.Contains("sistem klasörü"), dir + ": " + error);
            }

            // Korunan bir klasörün üstü de reddedilir (burada geçici klasörde taklit edilir)
            string fakeSystem = Path.Combine(_root, "Sys", "Windows");
            Directory.CreateDirectory(fakeSystem);
            Assert.Contains("sistem klasörü", Setup.CheckInstallFolder(Path.Combine(_root, "Sys"), new[] { fakeSystem }, NoKnownFiles, out _));
            Assert.Null(Setup.CheckInstallFolder(Path.Combine(fakeSystem, "POps"), new[] { fakeSystem }, NoKnownFiles, out string inside));
            Assert.Equal(Setup.StateNew, inside);
        }

        [Fact]
        public void DefaultLocation_IsAccepted()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "POps");
            Assert.Null(Setup.CheckInstallFolder(dir, Setup.DefaultProtectedFolders(), NoKnownFiles, out string state));
            Assert.NotNull(state);
        }

        [Fact]
        public void NewFolder_IsNew_AndPopsOnlyFolder_IsPops()
        {
            string fresh = Path.Combine(_root, "Apps", "POps");
            Directory.CreateDirectory(Path.GetDirectoryName(fresh));
            Assert.Null(Setup.CheckInstallFolder(fresh, new string[0], NoKnownFiles, out string state));
            Assert.Equal(Setup.StateNew, state);

            Directory.CreateDirectory(fresh);
            Write(Path.Combine(fresh, "POpsAgent.exe"), "x");
            Write(Path.Combine(fresh, "Microsoft.Extensions.Hosting.dll"), "x");
            Write(Path.Combine(fresh, "appsettings.json"), "{}");
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Microsoft.Extensions.Hosting.dll" };
            Assert.Null(Setup.CheckInstallFolder(fresh, new string[0], known, out state));
            Assert.Equal(Setup.StatePops, state);
        }

        // Kullanıcıların yazabildiği ve başka dosyalar içeren klasör reddedilir; yazamıyorlarsa dokunulmadan kurulur
        [Fact]
        public void ForeignFolder_IsRejectedOnlyWhenUsersCanWrite()
        {
            string shared = Path.Combine(_root, "Shared", "Tools");
            Directory.CreateDirectory(shared);
            Write(Path.Combine(shared, "baska-program.exe"), "x");
            Assert.Null(Setup.CheckInstallFolder(shared, new string[0], NoKnownFiles, out string state));
            Assert.Equal(Setup.StateOther, state);

            GrantAuthenticatedUsers(shared, FileSystemRights.Modify);
            string error = Setup.CheckInstallFolder(shared, new string[0], NoKnownFiles, out _);
            Assert.Contains("başka dosyalar", error);

            // Configure (SYSTEM) da savunma olarak durur ve izinlere dokunmaz
            string configureError = Setup.ApplyInstallFolderPolicy(shared, Setup.StateOther, _log.Add);
            Assert.Contains("POps'a ait değil", configureError);
            Assert.False(Directory.GetAccessControl(shared).AreAccessRulesProtected);
        }

        // Kullanıcı bir üst klasörü silip yeniden adlandırabiliyorsa (yerine kendi klasörünü koyabilir) kurulmaz
        [Fact]
        public void UserReplaceableParent_IsRejected()
        {
            string parent = Path.Combine(_root, "OpenParent");
            Directory.CreateDirectory(parent);
            GrantAuthenticatedUsers(parent, FileSystemRights.Modify);
            Assert.Contains("üst klasörü", Setup.CheckInstallFolder(Path.Combine(parent, "POps"), new string[0], NoKnownFiles, out _));

            // Henüz olmayan ara klasör, kullanıcıların değiştirebileceği izinleri devralacaksa da
            string inheriting = Path.Combine(_root, "InheritParent");
            Directory.CreateDirectory(inheriting);
            GrantAuthenticatedUsers(inheriting, FileSystemRights.Modify, PropagationFlags.InheritOnly);
            Assert.Contains("oluşturulunca", Setup.CheckInstallFolder(Path.Combine(inheriting, "Apps", "POps"), new string[0], NoKnownFiles, out _));
        }

        // Zaten korunan klasöre (ör. Program Files) dokunulmaz
        [Fact]
        public void ProtectedFolder_IsLeftAlone()
        {
            string dir = Path.Combine(_root, "Protected");
            Directory.CreateDirectory(dir);
            Assert.Null(Setup.ApplyInstallFolderPolicy(dir, Setup.StateOther, _log.Add));
            Assert.False(Directory.GetAccessControl(dir).AreAccessRulesProtected);
        }

        [Fact]
        public void Upgrade_WithoutProperties_KeepsSettingsAndPreservesUnknownKeys()
        {
            Layout layout = NewLayout();
            Write(Path.Combine(InstallDir, "appsettings.json"), "{\"ServerUrl\":\"https://pops.example\",\"Logging\":{\"LogLevel\":{\"Default\":\"Warning\"}},\"Custom\":[1,2,true,null]}");
            Assert.Null(Configure(layout));
            var config = Json(Path.Combine(InstallDir, "appsettings.json"));
            Assert.Equal("https://pops.example", config["ServerUrl"]);
            Assert.Equal("Warning", ((Dictionary<string, object>)((Dictionary<string, object>)config["Logging"])["LogLevel"])["Default"]);
            Assert.Equal(4, ((object[])config["Custom"]).Length);
        }

        [Fact]
        public void ExplicitProperties_Win()
        {
            Layout layout = NewLayout();
            Write(Path.Combine(InstallDir, "appsettings.json"), "{\"ServerUrl\":\"https://old.example\"}");
            Assert.Null(Configure(layout, ("SERVER_URL", "https://new.example/"), ("PERSIST_DIR", @"T:\POps"), ("BYPASS_SECRET", "new-bp")));
            var config = Json(Path.Combine(InstallDir, "appsettings.json"));
            Assert.Equal("https://new.example", config["ServerUrl"]);
            Assert.Equal(@"T:\POps", config["PersistDir"]);
            Assert.Equal("new-bp", File.ReadAllText(Secure(layout, "bypass.secret")));
        }

        [Fact]
        public void SecretFiles_WinOverDirectProperties()
        {
            Layout layout = NewLayout();
            string enroll = Path.Combine(_root, "enroll.txt");
            string bypass = Path.Combine(_root, "bypass.txt");
            Write(enroll, "AbCdEfGhIjKlMnOpQrStUvWxYz012345\r\n");
            Write(bypass, "file-bypass\r\n");

            Assert.Null(Configure(layout,
                ("SERVER_URL", "https://pops.example"),
                ("ENROLL_TOKEN", "ZZZZZZZZZZZZZZZZZZZZZZZZ"),
                ("ENROLL_TOKEN_FILE", enroll),
                ("BYPASS_SECRET", "direct-bypass"),
                ("BYPASS_SECRET_FILE", bypass)));

            Assert.Equal("AbCdEfGhIjKlMnOpQrStUvWxYz012345", File.ReadAllText(Secure(layout, "enroll.token")));
            Assert.Equal("file-bypass", File.ReadAllText(Secure(layout, "bypass.secret")));
            Assert.DoesNotContain(_log, line => line.Contains("AbCdEfGh") || line.Contains("file-bypass"));
        }

        [Theory]
        [InlineData("ENROLL_TOKEN_FILE")]
        [InlineData("BYPASS_SECRET_FILE")]
        public void UnreadableSecretFile_StopsBeforeWriting(string property)
        {
            Layout layout = NewLayout();
            string missing = Path.Combine(_root, "missing.secret");
            string error = Configure(layout, ("SERVER_URL", "https://pops.example"), (property, missing));

            Assert.Contains(property + " dosyası okunamadı", error);
            Assert.False(File.Exists(Path.Combine(InstallDir, "appsettings.json")));
            Assert.False(Directory.Exists(layout.SecureDir));
        }

        [Theory]
        [InlineData("SERVER_URL", "pops.example", "http")]
        [InlineData("SERVER_URL", "http://10.0.0.5:8000", "şifresiz http")]
        [InlineData("ENROLL_TOKEN", "bad token!", "ENROLL_TOKEN")]
        [InlineData("PERSIST_DIR", "relative", "PERSIST_DIR")]
        [InlineData("TERMINAL_ENABLED", "maybe", "TERMINAL_ENABLED")]
        [InlineData("VISION_ENABLED", "2", "VISION_ENABLED")]
        [InlineData("EXAM_ENABLED", "evetmi", "EXAM_ENABLED")]
        [InlineData("FILES_ENABLED", "x", "FILES_ENABLED")]
        public void InvalidProperty_FailsTheInstall(string key, string value, string expected)
        {
            Layout layout = NewLayout();
            Write(Path.Combine(InstallDir, "appsettings.json"), "{\"ServerUrl\":\"https://pops.example\"}");
            string error = Configure(layout, (key, value));
            Assert.NotNull(error);
            Assert.Contains(expected, error);
        }

        [Fact]
        public void FirstInstall_WithoutServerUrl_Fails()
        {
            Assert.Contains("SERVER_URL verilmedi", Configure(NewLayout()));
        }

        [Fact]
        public void HttpAddressFromAnOldInstall_IsRejected_NothingWritten()
        {
            Layout layout = NewLayout();
            Write(Path.Combine(layout.LegacyDirs[0], "appsettings.json"), "{\"ServerUrl\":\"http://10.0.0.5:8000\"}");
            Assert.Contains("SERVER_URL=https://", Configure(layout));
            Assert.False(File.Exists(Path.Combine(InstallDir, "appsettings.json")));
        }

        [Theory]
        [InlineData("http://127.0.0.1:8000")]
        [InlineData("http://localhost:8000")]
        [InlineData("https://pops.example")]
        public void SecureOrLoopbackServerUrl_IsAccepted(string url) => Assert.Null(Configure(NewLayout(), ("SERVER_URL", url)));

        // ---- yetenek politikası: güncelleme durumu korur ----
        [Fact]
        public void Capabilities_FirstInstallEnablesBoth()
        {
            Layout layout = NewLayout();
            Assert.Null(Configure(layout, ("SERVER_URL", "https://pops.example")));
            var caps = Json(Secure(layout, "capabilities.json"));
            Assert.Equal(true, caps["terminal_enabled"]);
            Assert.Equal(true, caps["vision_enabled"]);
            Assert.Equal(true, caps["exam_enabled"]);
            Assert.True(LockedDown(File.GetAccessControl(Secure(layout, "capabilities.json"))));
        }

        // Sınav modu yerelde kapatılabilir; dosyada olmayan (eski kurulum) yetenek açık sayılır ve korunur
        [Fact]
        public void Capabilities_ExamCanBeTurnedOffLocally_AndOldFilesKeepIt()
        {
            Layout layout = NewLayout();
            Assert.Null(Configure(layout, ("SERVER_URL", "https://pops.example"), ("EXAM_ENABLED", "0"), ("FILES_ENABLED", "0")));
            Assert.Equal(false, Json(Secure(layout, "capabilities.json"))["exam_enabled"]);
            Assert.Equal(false, Json(Secure(layout, "capabilities.json"))["files_enabled"]);

            Layout old = NewLayout();
            Write(Secure(old, "capabilities.json"), "{\"terminal_enabled\":false,\"vision_enabled\":true,\"source\":\"msi\"}");
            Assert.Null(Configure(old, ("SERVER_URL", "https://pops.example"), ("VISION_ENABLED", "1")));
            var caps = Json(Secure(old, "capabilities.json"));
            Assert.Equal(false, caps["terminal_enabled"]);
            Assert.Equal(true, caps["exam_enabled"]);
            Assert.Equal(true, caps["files_enabled"]);
        }

        [Fact]
        public void Capabilities_UpdateWithoutPropertiesKeepsServerDisabledState()
        {
            Layout layout = NewLayout();
            Assert.Null(Configure(layout, ("SERVER_URL", "https://pops.example")));
            // Sunucu terminali ve Vision'ı kapatmıştı
            Write(Secure(layout, "capabilities.json"), "{\"terminal_enabled\":false,\"vision_enabled\":false,\"source\":\"server\"}");

            Assert.Null(Configure(layout)); // güncelleme: özellik yok

            var caps = Json(Secure(layout, "capabilities.json"));
            Assert.Equal(false, caps["terminal_enabled"]);
            Assert.Equal(false, caps["vision_enabled"]);
        }

        [Fact]
        public void Capabilities_OnlyTheGivenFlagChanges()
        {
            Layout layout = NewLayout();
            Assert.Null(Configure(layout, ("SERVER_URL", "https://pops.example")));
            Write(Secure(layout, "capabilities.json"), "{\"terminal_enabled\":true,\"vision_enabled\":false,\"source\":\"server\"}");

            Assert.Null(Configure(layout, ("TERMINAL_ENABLED", "0")));
            var caps = Json(Secure(layout, "capabilities.json"));
            Assert.Equal(false, caps["terminal_enabled"]);
            Assert.Equal(false, caps["vision_enabled"]); // verilmeyen bayrak sunucunun kapattığı gibi kalır

            Assert.Null(Configure(layout, ("VISION_ENABLED", "evet"))); // yerel yönetici yeniden açabilir
            Assert.Equal(true, Json(Secure(layout, "capabilities.json"))["vision_enabled"]);
        }

        [Fact]
        public void Capabilities_UnreadableFile_MissingFlagStaysDisabled()
        {
            Layout layout = NewLayout();
            Assert.Null(Configure(layout, ("SERVER_URL", "https://pops.example")));
            Write(Secure(layout, "capabilities.json"), "{ broken");
            Assert.Null(Configure(layout, ("VISION_ENABLED", "1")));
            var caps = Json(Secure(layout, "capabilities.json"));
            Assert.Equal(false, caps["terminal_enabled"]);
            Assert.Equal(true, caps["vision_enabled"]);
        }

        // ---- eski kurulum temizliği, kaldırma, paket saklama ----
        [Fact]
        public void CleanupLegacy_RemovesOldInstall_KeepsUnrelatedFiles()
        {
            Layout layout = NewLayout();
            string cpops = layout.LegacyDirs[0], x86 = layout.LegacyDirs[1];
            Write(Path.Combine(cpops, "appsettings.json"), "{}");
            Write(Path.Combine(cpops, "notes.txt"), "admin notes");
            foreach (string f in new[] { "POpsAgent.exe", "PashaCoreAgent.dll", "apply_update.bat" }) Write(Path.Combine(x86, f), "x");
            Write(Path.Combine(x86, "runtimes", "win", "x.dll"), "x");
            File.SetAttributes(Path.Combine(x86, "apply_update.bat"), FileAttributes.ReadOnly);
            Write(Path.Combine(InstallDir, "PashaCoreAgent.exe"), "junk");
            Write(Path.Combine(InstallDir, "POpsAgent.dll"), "real");

            Setup.CleanupLegacy(InstallDir, layout, _log.Add);

            Assert.False(Directory.Exists(x86));
            Assert.False(File.Exists(Path.Combine(cpops, "appsettings.json")));
            Assert.True(File.Exists(Path.Combine(cpops, "notes.txt")));
            Assert.False(File.Exists(Path.Combine(InstallDir, "PashaCoreAgent.exe")));
            Assert.True(File.Exists(Path.Combine(InstallDir, "POpsAgent.dll")));
        }

        [Fact]
        public void RemoveConfig_DeletesAppSettingsOnly()
        {
            Layout layout = NewLayout();
            Assert.Null(Configure(layout, ("SERVER_URL", "https://pops.example"), ("BYPASS_SECRET", "bp")));
            Setup.RemoveConfig(InstallDir, _log.Add);
            Assert.False(File.Exists(Path.Combine(InstallDir, "appsettings.json")));
            Assert.True(File.Exists(Secure(layout, "bypass.secret")));
        }

        [Fact]
        public void KeepPackage_StoresTheInstalledMsi()
        {
            Layout layout = NewLayout();
            string msi = Path.Combine(_root, "dl", "POps-Agent-0.1.3-win-x64.msi");
            Write(msi, "v1");
            Setup.KeepPackage(msi, layout, _log.Add);
            string kept = Path.Combine(layout.DataDir, "packages", "installed.msi");
            Assert.Equal("v1", File.ReadAllText(kept));
            Write(msi, "v2");
            Setup.KeepPackage(msi, layout, _log.Add);
            Assert.Equal("v2", File.ReadAllText(kept));
            Setup.KeepPackage(kept, layout, _log.Add); // kendisinden yeniden kurulum: dokunulmaz
            Assert.Equal("v2", File.ReadAllText(kept));
        }

        [Fact]
        public void JsonWriter_RoundTripsWithInvariantCulture()
        {
            string json = Setup.ToJson(new Dictionary<string, object> { ["a"] = "x\"y\\z", ["b"] = 1.5m, ["c"] = new object[0], ["d"] = new Dictionary<string, object>() }, 0);
            var back = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(json);
            Assert.Equal("x\"y\\z", back["a"]);
            Assert.Contains("1.5", json);
        }
    }
}
