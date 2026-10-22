using System;
using System.Collections.Generic;
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
            Assert.Equal(2, POpsHelpers.DefaultConfigPaths.Count);
            Assert.Equal(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), POpsHelpers.DefaultConfigPaths[0]);
            Assert.Equal(@"C:\POps\appsettings.json", POpsHelpers.DefaultConfigPaths[1]);
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
            string[] configPaths = { Write("{\"ServerUrl\":\"https://pops.example/\"}") };
            Assert.Equal("https://pops.example", POpsHelpers.GetServerUrl(configPaths));
        }

        // Ortam değişkeni testin kendi sözlüğündedir: sürecin ortamı değişmez
        [Fact]
        public void GetSetting_EnvironmentVariableWins()
        {
            string name = "POPS_TEST_" + Guid.NewGuid().ToString("N");
            string[] configPaths = { Write("{\"Key\":\"from-file\"}") };
            var environment = new Dictionary<string, string>();
            string Variable(string key) => environment.TryGetValue(key, out string value) ? value : null;

            Assert.Equal("from-file", POpsHelpers.GetSetting("Key", name, configPaths, Variable));
            environment[name] = " from-env ";
            Assert.Equal("from-env", POpsHelpers.GetSetting("Key", name, configPaths, Variable));
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
            Assert.Equal(userProfile, POpsHelpers.LogsToUserProfileFor(component));
            Assert.Equal(fileName, Path.GetFileName(POpsHelpers.LogFilePath(component, new DateTime(2026, 9, 27))));
        }
    }
}
