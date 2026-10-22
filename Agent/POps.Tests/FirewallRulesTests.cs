using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using POps.Shared;
using Xunit;

namespace POps.Tests
{
    // POps'un güvenlik duvarı kurallarının tek listesi (POps.Shared.FirewallRules) ve MSI kaldırmasının betiği. Dosya ajanda ve
    // MSI custom action'larında (bağlantılı derleme) derlenir: testler iki hedefte de çalışır. Betik hiç çalıştırılmaz.
    public class FirewallRulesTests : TestBase
    {
        private static string Quoted(string value) => "'" + value.Replace("'", "''") + "'";

        private static List<string> Constants(string suffix) =>
            typeof(FirewallRules).GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f.IsLiteral && f.FieldType == typeof(string) && f.Name.EndsWith(suffix, StringComparison.Ordinal))
                .Select(f => (string)f.GetRawConstantValue())
                .ToList();

        // Bir grup sabiti listeye (Groups) eklenmezse kaldırma onu silmezdi
        [Fact]
        public void Groups_ListEveryGroupConstantOnce()
        {
            List<string> constants = Constants("Group");
            Assert.Equal(3, constants.Count);
            Assert.Equal(constants.OrderBy(g => g, StringComparer.Ordinal), FirewallRules.Groups.OrderBy(g => g, StringComparer.Ordinal));
            Assert.Equal(FirewallRules.Groups.Count, FirewallRules.Groups.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.All(FirewallRules.Groups, g => Assert.True(FirewallRules.IsGroup(g)));
            Assert.False(FirewallRules.IsGroup("POps Kiosk"));
            Assert.False(FirewallRules.IsGroup(null));
        }

        [Fact]
        public void StateFiles_ListEveryStateFileConstant()
        {
            List<string> constants = Constants("File");
            Assert.Equal(new[] { "exam.json", "isolation.json", "lockdown.json" }, constants.OrderBy(f => f, StringComparer.Ordinal));
            Assert.Equal(constants.OrderBy(f => f, StringComparer.Ordinal), FirewallRules.StateFiles.OrderBy(f => f, StringComparer.Ordinal));
        }

        // Liste betikten türetilir: Groups'a eklenen her grup kaldırmada silinir
        [Fact]
        public void UninstallScript_RemovesEveryGroupAndTheLegacyRules()
        {
            string script = FirewallRules.BuildUninstallScript(null);
            string groups = script.Split('\n').Single(l => l.StartsWith("$groups = @(", StringComparison.Ordinal)).Trim();
            Assert.Equal("$groups = @(" + string.Join(", ", FirewallRules.Groups.Select(Quoted)) + ")", groups);
            foreach (string group in FirewallRules.Groups) Assert.Contains(Quoted(group), groups);
            Assert.Contains("$legacy = 'POps_Isolation_*'", script);
            Assert.Contains("Get-NetFirewallRule | Where-Object { ($groups -contains $_.Group) -or ($_.DisplayName -like $legacy) }", script);
            Assert.Contains("try { $rule | Remove-NetFirewallRule; $removed++ }", script);
            Assert.StartsWith("$ErrorActionPreference = 'Stop'", script, StringComparison.Ordinal);
            // Sahada (0.1.24): ilerleme kayıtları MSI log'undaki sonuç satırına karışıyordu
            Assert.Contains("$ProgressPreference = 'SilentlyContinue'", script);
            // Kaldırma hiçbir profili açmaz, hiçbir kural eklemez
            Assert.DoesNotContain("-Enabled True", script);
            Assert.DoesNotContain("New-NetFirewallRule", script);
        }

        [Fact]
        public void UninstallScript_SwitchesOffOnlyKnownProfiles_Once()
        {
            string script = FirewallRules.BuildUninstallScript(new[] { "Public", " private ", "Evil'; Remove-Item C:\\ -Recurse #", "Public", null, "" });
            Assert.Single(script.Split('\n'), l => l.Contains("Set-NetFirewallProfile -Profile Public -Enabled False"));
            Assert.Single(script.Split('\n'), l => l.Contains("Set-NetFirewallProfile -Profile Private -Enabled False"));
            Assert.DoesNotContain("-Profile Domain", script);
            Assert.DoesNotContain("Evil", script);
            Assert.DoesNotContain("Remove-Item", script);
            Assert.Equal(new[] { "Public", "Private" }, FirewallRules.KnownProfiles(new[] { "public", "Private", "Bogus", "PUBLIC" }));
        }

        [Fact]
        public void UninstallScript_WithoutProfiles_LeavesThemAlone()
        {
            Assert.DoesNotContain("Set-NetFirewallProfile", FirewallRules.BuildUninstallScript(new string[0]));
            Assert.DoesNotContain("Set-NetFirewallProfile", FirewallRules.BuildUninstallScript(null));
        }

        // Sıra: kurallar silinir, profiller geri döner, sonra kalan kural aranır; sorun varsa çıkış 2 (kaldırma bunu loglar ve
        // durum dosyalarını bırakır)
        [Fact]
        public void UninstallScript_ChecksForLeftoversLast_AndFailsWithExitCode2()
        {
            string script = FirewallRules.BuildUninstallScript(new[] { "Public" });
            int remove = script.IndexOf("Remove-NetFirewallRule", StringComparison.Ordinal);
            int profile = script.IndexOf("Set-NetFirewallProfile -Profile Public -Enabled False", StringComparison.Ordinal);
            int check = script.IndexOf("$left = @(Get-POpsRules)", StringComparison.Ordinal);
            int fail = script.IndexOf("if ($problems.Count -gt 0) { $problems -join '; '; exit 2 }", StringComparison.Ordinal);
            int ok = script.IndexOf("'OK ' + $removed", StringComparison.Ordinal);
            Assert.True(remove > 0 && remove < profile && profile < check && check < fail && fail < ok, script);
            Assert.Contains("$problems += 'left: '", script);
        }
    }
}
