using System;
using System.IO;
using POps.Shared;
using Xunit;

namespace POps.Tests.Agent
{
    public class POpsHelpersTests : TestBase
    {
        private static string Write(string json)
        {
            string path = Path.Combine(TestEnvironment.NewDir("cfg"), "appsettings.json");
            File.WriteAllText(path, json);
            return path;
        }

        // v0.1.3 hatasının tekrarlamaması: ayar önce kurulum klasöründen, sonra C:\POps'tan okunur
        [Fact]
        public void DefaultConfigPaths_InstallFolderFirst_ThenLegacy()
        {
            Assert.Equal(2, TestEnvironment.DefaultConfigPaths.Length);
            Assert.Equal(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), TestEnvironment.DefaultConfigPaths[0]);
            Assert.Equal(@"C:\POps\appsettings.json", TestEnvironment.DefaultConfigPaths[1]);
        }

        [Fact]
        public void ReadConfigValue_FirstNonEmptyValueWins()
        {
            string install = Write("{\"ServerUrl\":\"https://install.example\"}");
            string legacy = Write("{\"ServerUrl\":\"https://legacy.example\"}");
            Assert.Equal("https://install.example", POpsHelpers.ReadConfigValue("ServerUrl", new[] { install, legacy }));
        }

        [Theory]
        [InlineData("{\"ServerUrl\":\"\"}")]
        [InlineData("{\"ServerUrl\":\"   \"}")]
        [InlineData("{\"Other\":\"x\"}")]
        [InlineData("{ broken")]
        public void ReadConfigValue_FallsThroughEmptyMissingOrBrokenFiles(string first)
        {
            string install = Write(first);
            string legacy = Write("{\"ServerUrl\":\" https://legacy.example \"}");
            Assert.Equal("https://legacy.example", POpsHelpers.ReadConfigValue("ServerUrl", new[] { install, Path.Combine(TestEnvironment.Root, "missing.json"), legacy }));
        }

        [Fact]
        public void GetServerUrl_UsesConfigPaths_AndTrimsSlash()
        {
            POpsHelpers.ConfigPaths = new[] { Write("{\"ServerUrl\":\"https://pops.example/\"}") };
            try { Assert.Equal("https://pops.example", POpsHelpers.GetServerUrl()); }
            finally { POpsHelpers.ConfigPaths = new string[0]; }
        }

        [Fact]
        public void GetSetting_EnvironmentVariableWins()
        {
            string name = "POPS_TEST_" + Guid.NewGuid().ToString("N");
            POpsHelpers.ConfigPaths = new[] { Write("{\"Key\":\"from-file\"}") };
            try
            {
                Assert.Equal("from-file", POpsHelpers.GetSetting("Key", name));
                Environment.SetEnvironmentVariable(name, " from-env ");
                Assert.Equal("from-env", POpsHelpers.GetSetting("Key", name));
            }
            finally
            {
                Environment.SetEnvironmentVariable(name, null);
                POpsHelpers.ConfigPaths = new string[0];
            }
        }

        [Theory]
        [InlineData("https://pops.example", true)]
        [InlineData("http://127.0.0.1:8000", true)]
        [InlineData("http://localhost:8000", true)]
        [InlineData("http://10.0.0.5:8000", false)]
        [InlineData("http://pops.example", false)]
        [InlineData("ftp://pops.example", false)]
        [InlineData("not a url", false)]
        public void IsSecureServerUrl(string url, bool expected) => Assert.Equal(expected, POpsHelpers.IsSecureServerUrl(url));

        [Fact]
        public void AppVersion_ComesFromTheRootVersionFile()
        {
            string version = File.ReadAllText(Path.Combine(TestEnvironment.RepoRoot(), "VERSION")).Trim();
            Assert.Equal("v" + version, POpsHelpers.AppVersion);
        }

        [Theory]
        [InlineData("Agent", false, "POps_20260927.log")]
        [InlineData("Updater", false, "POps_20260927.log")]
        [InlineData("Watchdog", true, "POpsWatchdog_20260927.log")]
        [InlineData("Vision", true, "POpsVision_20260927.log")]
        public void LogFile_PerComponent(string component, bool userProfile, string fileName)
        {
            string original = POpsHelpers.Component;
            try
            {
                POpsHelpers.Component = component;
                Assert.Equal(userProfile, POpsHelpers.LogsToUserProfile);
                Assert.Equal(fileName, Path.GetFileName(POpsHelpers.LogFilePath(new DateTime(2026, 9, 27))));
            }
            finally { POpsHelpers.Component = original; }
        }
    }
}
