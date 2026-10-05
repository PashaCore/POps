using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Web.Script.Serialization;
using POps.Installer;
using POps.Shared;
using Xunit;

namespace POps.Tests.Installer
{
    // MSI, appsettings.json'daki DataDirectory / LogDirectory'yi ajanla aynı kuralla kullanır: gizli değerler, yetenekler,
    // geri dönüş paketi ve karantina kaydı seçilen veri klasörüne gider. Bütün klasörler geçici test klasöründe.
    public class FolderSetupTests : TestBase
    {
        private readonly string _root = TestEnvironment.NewDir("msi-folders");
        private readonly List<string> _log = new List<string>();
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        public FolderSetupTests() => Setup.TrustedBaseForTests = _root;

        private Layout NewLayout() => new Layout
        {
            DataDir = Path.Combine(_root, "POpsData"),
            SecureDir = Path.Combine(_root, "POpsData", "secure"),
            LogDir = Path.Combine(_root, "POpsLogs"),
            LegacyDirs = new[] { Path.Combine(_root, "C_POps") },
            Rules = new FolderRules { TrustedBase = _root },
        };

        private string InstallDir => Path.Combine(_root, "PF", "POps");
        private string CustomData => Path.Combine(_root, "D", "Kurum", "POpsData");
        private string CustomLogs => Path.Combine(_root, "E", "POpsLogs");

        private static void WriteConfig(string dir, Dictionary<string, object> config)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "appsettings.json"), Json.Serialize(config));
        }

        private Dictionary<string, object> InstalledConfig() =>
            (Dictionary<string, object>)Json.DeserializeObject(File.ReadAllText(Path.Combine(InstallDir, "appsettings.json")));

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

        [Fact]
        public void Upgrade_WritesSecretsIntoTheConfiguredDataFolder()
        {
            WriteConfig(InstallDir, new Dictionary<string, object> { ["ServerUrl"] = "https://pops.example", ["DataDirectory"] = CustomData, ["LogDirectory"] = CustomLogs });
            Layout layout = NewLayout();

            Assert.Null(Configure(layout, ("ENROLL_TOKEN", "AbCdEfGhIjKlMnOpQrStUvWxYz012345"), ("TERMINAL_ENABLED", "0")));

            Assert.True(File.Exists(Path.Combine(CustomData, "secure", "enroll.token")));
            Assert.True(File.Exists(Path.Combine(CustomData, "secure", "capabilities.json")));
            Assert.False(Directory.Exists(layout.DataDir));
            Assert.False(Directory.Exists(layout.LogDir));

            var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
            DirectorySecurity data = Directory.GetAccessControl(CustomData);
            Assert.True(data.AreAccessRulesProtected);
            Assert.Contains(data.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(),
                r => r.IdentityReference.Equals(users) && (r.FileSystemRights & FileSystemRights.WriteData) == 0);
            foreach (string locked in new[] { Path.Combine(CustomData, "secure"), CustomLogs })
            {
                DirectorySecurity sec = Directory.GetAccessControl(locked);
                Assert.True(sec.AreAccessRulesProtected);
                Assert.DoesNotContain(sec.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(), r => r.IdentityReference.Equals(users));
            }

            Dictionary<string, object> config = InstalledConfig();
            Assert.Equal(CustomData, config["DataDirectory"]);
            Assert.Equal(CustomLogs, config["LogDirectory"]);
        }

        // Eski konumdaki (C:\POps) ayar taşınır: CleanupLegacy o dosyayı sildikten sonra ajan yine aynı klasörü kullansın
        [Fact]
        public void LegacySetting_IsCarriedIntoTheInstalledConfig()
        {
            Layout layout = NewLayout();
            WriteConfig(layout.LegacyDirs[0], new Dictionary<string, object> { ["ServerUrl"] = "https://pops.example", ["DataDirectory"] = CustomData });

            Assert.Null(Configure(layout));

            Assert.Equal(CustomData, InstalledConfig()["DataDirectory"]);
            Assert.False(InstalledConfig().ContainsKey("LogDirectory"));
            Assert.True(Directory.Exists(Path.Combine(CustomData, "secure")));
            Assert.True(Directory.Exists(layout.LogDir));
        }

        // Geçersiz ayar kurulumu durdurmaz: ajan gibi varsayılan klasör kullanılır, uyarı loglanır
        [Fact]
        public void InvalidSetting_UsesTheDefaultFolders_LikeTheAgent()
        {
            WriteConfig(InstallDir, new Dictionary<string, object> { ["ServerUrl"] = "https://pops.example", ["DataDirectory"] = @"\\server\share\POps" });
            Layout layout = NewLayout();

            Assert.Null(Configure(layout, ("BYPASS_SECRET", "bp")));

            Assert.True(File.Exists(Path.Combine(layout.SecureDir, "bypass.secret")));
            Assert.Contains(_log, l => l.Contains("UYARI") && l.Contains("DataDirectory geçersiz") && l.Contains("ağ yolu"));
        }

        [Fact]
        public void KeepPackageAndUninstall_UseTheConfiguredDataFolder()
        {
            WriteConfig(InstallDir, new Dictionary<string, object> { ["ServerUrl"] = "https://pops.example", ["DataDirectory"] = CustomData });
            Assert.Null(Configure(NewLayout()));

            Layout installed = Setup.ForInstall(NewLayout(), InstallDir, _log.Add);
            Assert.Equal(CustomData, installed.DataDir);
            Assert.Equal(Path.Combine(CustomData, "secure"), installed.SecureDir);
            Assert.Equal(NewLayout().LogDir, installed.LogDir);

            string msi = Path.Combine(_root, "dl", "POps-Agent-0.1.3-win-x64.msi");
            Directory.CreateDirectory(Path.GetDirectoryName(msi));
            File.WriteAllText(msi, "v1");
            Setup.KeepPackage(msi, installed, _log.Add);
            Assert.Equal("v1", File.ReadAllText(Path.Combine(CustomData, "packages", "installed.msi")));

            // appsettings.json yoksa (ya da ayar yoksa) varsayılanlar
            Assert.Equal(NewLayout().DataDir, Setup.ForInstall(NewLayout(), Path.Combine(_root, "yok"), _log.Add).DataDir);
        }
    }
}
