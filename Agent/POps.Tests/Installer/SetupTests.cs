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
            var data = new Dictionary<string, string> { ["INSTALLFOLDER"] = InstallDir + "\\" };
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

        [Theory]
        [InlineData("SERVER_URL", "pops.example", "http")]
        [InlineData("SERVER_URL", "http://10.0.0.5:8000", "şifresiz http")]
        [InlineData("ENROLL_TOKEN", "bad token!", "ENROLL_TOKEN")]
        [InlineData("PERSIST_DIR", "relative", "PERSIST_DIR")]
        [InlineData("TERMINAL_ENABLED", "maybe", "TERMINAL_ENABLED")]
        [InlineData("VISION_ENABLED", "2", "VISION_ENABLED")]
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
            Assert.True(LockedDown(File.GetAccessControl(Secure(layout, "capabilities.json"))));
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
