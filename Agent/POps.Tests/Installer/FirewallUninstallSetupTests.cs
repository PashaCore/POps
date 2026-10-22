using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Xml.Linq;
using POps.Installer;
using POps.Shared;
using Xunit;

namespace POps.Tests.Installer
{
    // MSI kaldırılırken (RemoveConfig) POps'un güvenlik duvarı kuralları silinir, karantinanın / sınavın açtığı profiller geri döner
    // ve karantina / sınav durum dosyaları gider. Betik hiç çalıştırılmaz: Setup.RemoveFirewallRules'a sahte bir çalıştırıcı verilir;
    // klasörler geçicidir.
    public class FirewallUninstallSetupTests : TestBase
    {
        private readonly string _root = TestEnvironment.NewDir("msi-firewall");
        private readonly List<string> _log = new List<string>();
        private readonly List<string> _scripts = new List<string>();

        private Layout NewLayout()
        {
            var layout = new Layout
            {
                DataDir = Path.Combine(_root, "POpsData"),
                SecureDir = Path.Combine(_root, "POpsData", "secure"),
                LogDir = Path.Combine(_root, "POpsLogs"),
                LegacyDirs = new string[0],
            };
            Directory.CreateDirectory(layout.SecureDir);
            return layout;
        }

        private Func<string, (int Exit, string Output)> Runner(int exit, string output) => script =>
        {
            _scripts.Add(script);
            return (exit, output);
        };

        private static string Secure(Layout layout, string name) => Path.Combine(layout.SecureDir, name);

        // Ajanın yazdığı biçim (bkz. FirewallCleanupCoverageTests.TheAgentWritesTheStateTheUninstallReads): karantina önceki
        // profil durumlarını, sınav modu önceden kapalı profillerin adlarını tutar
        private static void WriteQuarantineAndExam(Layout layout)
        {
            File.WriteAllText(Secure(layout, FirewallRules.IsolationStateFile), "{\"" + FirewallRules.IsolationProfilesKey +
                "\":[{\"Name\":\"Domain\",\"Enabled\":\"True\"},{\"Name\":\"Private\",\"Enabled\":\"False\"},{\"Name\":\"Public\",\"Enabled\":\"True\"}]," +
                "\"since\":1790000000,\"server_addresses\":[\"203.0.113.10\"]}");
            File.WriteAllText(Secure(layout, FirewallRules.ExamStateFile), "{\"allow\":[\"10.0.0.5\"],\"until\":1790003600,\"message\":\"\",\"block_apps\":[]," +
                "\"since\":1790000000,\"addresses\":[\"203.0.113.10\"],\"" + FirewallRules.ExamProfilesKey + "\":[\"Public\"]}");
            File.WriteAllText(Secure(layout, FirewallRules.QuarantineLockFile), "{\"reason\":\"Belirtilmedi\"}");
        }

        [Fact]
        public void QuarantineAndExam_TheRulesGo_TheProfilesComeBack_TheStateGoes()
        {
            Layout layout = NewLayout();
            WriteQuarantineAndExam(layout);
            File.WriteAllText(Secure(layout, "bypass.secret"), "bp");   // kimlik ve gizli değerler kalır

            Assert.True(Setup.RemoveFirewallRules(layout, Runner(0, "OK 5"), _log.Add));

            string script = Assert.Single(_scripts);
            Assert.Equal(FirewallRules.BuildUninstallScript(new[] { "Private", "Public" }), script);
            foreach (string group in FirewallRules.Groups) Assert.Contains("'" + group + "'", script);
            Assert.Contains("Set-NetFirewallProfile -Profile Private -Enabled False", script);
            Assert.Contains("Set-NetFirewallProfile -Profile Public -Enabled False", script);
            Assert.DoesNotContain("-Profile Domain", script);
            foreach (string name in FirewallRules.StateFiles) Assert.False(File.Exists(Secure(layout, name)), name);
            Assert.True(File.Exists(Secure(layout, "bypass.secret")));
            Assert.Contains(_log, l => l.Contains("güvenlik duvarı kuralları kaldırıldı") && l.Contains("OK 5") && l.Contains("Private, Public"));
        }

        // Karantina yokken de çalışır: eş önbelleğinin ya da eski bir sürümün kuralı kalmış olabilir; profillere dokunulmaz
        [Fact]
        public void NoQuarantine_StillRemovesTheRules_AndLeavesTheProfiles()
        {
            Layout layout = NewLayout();
            Assert.True(Setup.RemoveFirewallRules(layout, Runner(0, "OK 1"), _log.Add));
            string script = Assert.Single(_scripts);
            Assert.Contains("Remove-NetFirewallRule", script);
            Assert.DoesNotContain("Set-NetFirewallProfile", script);
        }

        // Kurallar kalkmadıysa durum dosyaları kalır: yeniden kurulan ajan karantinayı bilir, panelin "unlock"u kuralları kaldırır
        [Fact]
        public void ScriptFails_KeepsTheState_AndLogsTheManualSteps()
        {
            Layout layout = NewLayout();
            WriteQuarantineAndExam(layout);
            Assert.False(Setup.RemoveFirewallRules(layout, Runner(2, "left: POps Isolation - Outbound"), _log.Add));
            foreach (string name in FirewallRules.StateFiles) Assert.True(File.Exists(Secure(layout, name)), name);
            Assert.Contains(_log, l => l.Contains("HATA") && l.Contains("çıkış 2") && l.Contains("left: POps Isolation - Outbound") && l.Contains("Agent/README.md"));
        }

        [Fact]
        public void PowerShellMissing_DoesNotStopTheUninstall()
        {
            Layout layout = NewLayout();
            WriteQuarantineAndExam(layout);
            Assert.False(Setup.RemoveFirewallRules(layout, _ => throw new Win32Exception(2), _log.Add));
            Assert.True(File.Exists(Secure(layout, FirewallRules.IsolationStateFile)));
            Assert.Contains(_log, l => l.Contains("HATA") && l.Contains("Win32Exception"));
        }

        // Okunamayan ya da beklenmeyen durum: profiller açık kalır (kapatılacağı bilinmiyor), kurallar yine silinir; betiğe
        // dosyadan yalnızca bilinen profil adları girer
        [Fact]
        public void UnreadableOrForeignState_LeavesTheProfilesOn()
        {
            Layout layout = NewLayout();
            File.WriteAllText(Secure(layout, FirewallRules.IsolationStateFile), "{ broken");
            File.WriteAllText(Secure(layout, FirewallRules.ExamStateFile), "{\"" + FirewallRules.ExamProfilesKey + "\":[\"Evil'; Remove-Item C:\\\\ -Recurse #\",5,null]}");
            Assert.True(Setup.RemoveFirewallRules(layout, Runner(0, "OK 0"), _log.Add));
            string script = Assert.Single(_scripts);
            Assert.DoesNotContain("Set-NetFirewallProfile", script);
            Assert.DoesNotContain("Evil", script);
            Assert.DoesNotContain("Remove-Item", script);
            Assert.Contains(_log, l => l.Contains("okunamadı"));
            Assert.Empty(Setup.PreviouslyDisabledProfiles(layout, _ => { }));
        }

        // Custom action'ın kendisi (Session ister, test çalıştıramaz): RemoveConfig temizliği PowerShell çalıştırıcısıyla çağırır
        [Fact]
        public void RemoveConfigAction_RunsTheFirewallCleanupWithPowerShell()
        {
            MethodInfo action = typeof(CustomActions).GetMethod(nameof(CustomActions.RemoveConfig));
            List<string> callees = StaticAccessScanner.DirectCallees(action).Select(m => m.DeclaringType.Name + "." + m.Name).ToList();
            Assert.Contains("Setup.RemoveFirewallRules", callees);
            Assert.Contains("FirewallScript.Run", callees);
            Assert.Contains("Setup.RestoreKiosk", callees);
            Assert.Contains("Setup.RemoveConfig", callees);
        }

        // Çalıştırıcının komut satırı (süreç başlatılmaz): 64 bit Windows PowerShell, profil yok, betik -EncodedCommand ile
        [Fact]
        public void FirewallScript_StartsWindowsPowerShellWithTheEncodedScript()
        {
            string script = FirewallRules.BuildUninstallScript(new[] { "Public" });
            System.Diagnostics.ProcessStartInfo psi = FirewallScript.StartInfo(script);
            Assert.Equal(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"), psi.FileName);
            const string prefix = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand ";
            Assert.StartsWith(prefix, psi.Arguments, StringComparison.Ordinal);
            Assert.Equal(script, Encoding.Unicode.GetString(Convert.FromBase64String(psi.Arguments.Substring(prefix.Length))));
            Assert.False(psi.UseShellExecute);
            Assert.True(psi.CreateNoWindow && psi.RedirectStandardOutput && psi.RedirectStandardError);
            Assert.Equal(TimeSpan.FromMinutes(2), FirewallScript.Timeout);
        }

        // Kurallar yalnızca gerçek kaldırmada silinir: yükseltmede ve geri dönüşte eski ürün UPGRADINGPRODUCTCODE ile kaldırılır,
        // karantina ve sınav yeni sürümde sürer. Custom action SYSTEM olarak (güvenlik duvarı yönetici ister), servis durduktan
        // sonra (StopServices, RemoveFiles'tan önce gelir: servis kuralları yeniden kuramaz) çalışır; hatası kaldırmayı durdurmaz.
        [Fact]
        public void Package_RemovesTheRulesOnlyOnARealUninstall()
        {
            XDocument package = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "TestData", "AgentPackage.wxs"));
            IEnumerable<XElement> All(string name) => package.Descendants().Where(e => e.Name.LocalName == name);

            XElement action = All("CustomAction").Single(e => (string)e.Attribute("Id") == "PopsRemoveConfig");
            Assert.Equal("RemoveConfig", (string)action.Attribute("DllEntry"));
            Assert.Equal("deferred", (string)action.Attribute("Execute"));
            Assert.Equal("no", (string)action.Attribute("Impersonate"));
            Assert.Equal("ignore", (string)action.Attribute("Return"));

            XElement step = All("Custom").Single(e => (string)e.Attribute("Action") == "PopsRemoveConfig");
            Assert.Equal("REMOVE~=\"ALL\" AND NOT UPGRADINGPRODUCTCODE", (string)step.Attribute("Condition"));
            Assert.Equal("RemoveFiles", (string)step.Attribute("Before"));

            Assert.Contains("INSTALLFOLDER=[INSTALLFOLDER]", (string)All("SetProperty").Single(e => (string)e.Attribute("Id") == "PopsRemoveConfig").Attribute("Value"));
            Assert.Equal("both", (string)All("ServiceControl").Single(e => (string)e.Attribute("Name") == "POpsAgent").Attribute("Stop"));
            // Başka hiçbir adım RemoveConfig'i çalıştırmaz (ör. kurulumda ya da yükseltmede)
            Assert.Single(All("CustomAction"), e => (string)e.Attribute("DllEntry") == "RemoveConfig");
        }
    }
}
