using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using POps.Shared;
using POpsAgent;
using Xunit;

namespace POps.Tests.Agent
{
    // MSI kaldırması (RemoveConfig, POps.Shared.FirewallRules.BuildUninstallScript) ajanın eklediği her güvenlik duvarı kuralını
    // siler mi: ürün kodunda kural ekleyen ya da profil açan her betik burada bilinir (IL'deki dize sabitlerinden bulunur), her biri
    // FirewallRules.Groups'taki bir grupta kurar ve kaldırma betiği o grupları siler. Yeni bir grup ya da kural ekleyen yeni bir
    // betik buraya ve Groups'a eklenmeden testler geçmez. Kaldırmanın okuduğu durum dosyalarını ajanın aynı adlarla yazdığı da
    // denetlenir. Betikler hiç çalıştırılmaz (sahte çalıştırıcılar).
    public class FirewallCleanupCoverageTests : TestBase
    {
        // Güvenlik duvarını değiştiren betik parçaları: kural ekleme / açma ya da profil açma (netsh dahil)
        private static readonly string[] RuleChanges =
        {
            "New-NetFirewallRule", "Copy-NetFirewallRule", "Enable-NetFirewallRule", "Set-NetFirewallRule", "-Enabled True",
            "firewall add rule", "firewall set rule", "state on",
        };

        // Kural ekleyen ya da profil açan ürün yöntemleri ve kurabildikleri betikler. Yeni bir yöntem buraya eklenmeli: betiğinin
        // grubu Groups'ta olmalı (aşağıdaki denetimler). Karantina ve sınav modu aynı kurucuyu kendi gruplarıyla kullanır.
        private static Dictionary<string, IReadOnlyList<string>> RuleWriters()
        {
            var ranges = NetworkIsolation.AllowedRanges(new[] { IPAddress.Parse("203.0.113.10") });
            return new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["NetworkIsolation.BuildEnableScript"] = new[]
                {
                    NetworkIsolation.BuildEnableScript(ranges),
                    NetworkIsolation.BuildEnableScript(ranges, NetworkIsolation.ExamRuleGroup),
                },
                ["PeerCache.BuildAddRuleScript"] = new[] { PeerCache.BuildAddRuleScript(@"C:\Program Files\POps\POpsAgent.exe") },
            };
        }

        // Profil açan betiklerin grubu ve önceki profil durumunu kaydettiği dosya / alan (kaldırma oradan geri yükler)
        private static readonly Dictionary<string, (string File, string Key)> ProfileRecords = new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            [FirewallRules.IsolationGroup] = (FirewallRules.IsolationStateFile, FirewallRules.IsolationProfilesKey),
            [FirewallRules.ExamGroup] = (FirewallRules.ExamStateFile, FirewallRules.ExamProfilesKey),
        };

        private static string GroupOf(string script)
        {
            List<string> lines = script.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("$group = '", StringComparison.Ordinal)).ToList();
            Assert.True(lines.Count == 1, "Betik grubunu tek bir \"$group = '...'\" satırıyla vermeli:" + Environment.NewLine + script);
            return lines[0].Substring("$group = '".Length).TrimEnd('\'').Replace("''", "'");
        }

        [Fact]
        public void EveryMethodThatAddsARule_IsKnown()
        {
            var found = StaticAccessScanner.ScannedAssemblies()
                .Where(a => a.GetName().Name == "POpsAgent" || a.GetName().Name == "POps.Shared")
                .SelectMany(StaticAccessScanner.StringLiterals)
                .Where(l => RuleChanges.Any(c => l.Literal.Contains(c, StringComparison.Ordinal)))
                .Select(l => StaticAccessScanner.Describe(l.Method))
                .Distinct()
                .OrderBy(m => m, StringComparer.Ordinal)
                .ToList();
            Assert.Contains("NetworkIsolation.BuildEnableScript", found);   // tarama gerçekten bir şey buldu
            Assert.True(found.SequenceEqual(RuleWriters().Keys.OrderBy(m => m, StringComparer.Ordinal)),
                "Güvenlik duvarına kural ekleyen ya da profil açan yöntemler değişti. Yeni yöntemi RuleWriters'a ekleyin; kurallarını " +
                "POps.Shared.FirewallRules.Groups'taki bir grupta kursun (MSI kaldırması yalnızca o grupları siler): " + string.Join(", ", found));
        }

        [Fact]
        public void EveryRuleIsInAGroupThatTheUninstallRemoves()
        {
            string uninstall = FirewallRules.BuildUninstallScript(null);
            var groups = new HashSet<string>(StringComparer.Ordinal);
            foreach ((string method, IReadOnlyList<string> scripts) in RuleWriters())
                foreach (string script in scripts)
                {
                    string group = GroupOf(script);
                    groups.Add(group);
                    Assert.True(FirewallRules.IsGroup(group), $"{method}: \"{group}\" FirewallRules.Groups'ta yok");
                    Assert.Contains("'" + group + "'", uninstall);
                    // Her kural betiğin grubunda kurulur (grupsuz kural kaldırmada bulunamazdı)
                    foreach (string line in script.Split('\n').Where(l => l.Contains("New-NetFirewallRule", StringComparison.Ordinal)))
                        Assert.Contains("New-NetFirewallRule -Group $group ", line);
                    // Profil açan betik önceki durumu kaldırmanın okuduğu dosyaya yazar
                    if (script.Contains("-Enabled True", StringComparison.Ordinal))
                        Assert.True(ProfileRecords.ContainsKey(group), $"{method}: \"{group}\" profilleri açıyor ama önceki durumu kaldırmanın okuduğu bir dosyada değil");
                }
            // Listedeki her grubu ajan gerçekten kullanır (kullanılmayan grup kalmasın)
            Assert.Equal(FirewallRules.Groups.OrderBy(g => g, StringComparer.Ordinal), groups.OrderBy(g => g, StringComparer.Ordinal));
        }

        [Fact]
        public void TheAgentUsesTheSharedNames()
        {
            Assert.Equal(FirewallRules.IsolationGroup, NetworkIsolation.RuleGroup);
            Assert.Equal(FirewallRules.ExamGroup, NetworkIsolation.ExamRuleGroup);
            Assert.Equal(FirewallRules.PeerCacheGroup, PeerCache.RuleGroup);
            Assert.Contains("-DisplayName 'POps Peer Cache (TCP 8817)'", PeerCache.BuildAddRuleScript("x.exe"));
            Assert.Contains("Get-NetFirewallRule -Group 'POps Peer Cache'", PeerCache.BuildRemoveRuleScript());
            Assert.Contains($"-DisplayName '{FirewallRules.LegacyIsolationRules}'", NetworkIsolation.BuildDisableScript(new string[0]));
            // Listede olmayan bir grupla kural kurulmaz (kaldırma onu silmezdi)
            var ranges = NetworkIsolation.AllowedRanges(new[] { IPAddress.Parse("203.0.113.10") });
            Assert.Throws<ArgumentException>(() => NetworkIsolation.BuildEnableScript(ranges, "POps Kiosk"));
        }

        // Kural adları tek yerde: ürün kaynağında (ajan, ortak kod, MSI custom action'ları) grup adları ve eski karantina deseni
        // yalnızca FirewallRules.cs'te yazılıdır; başka her yer sabitleri kullanır (yorum satırları sayılmaz)
        [Fact]
        public void RuleNames_AreWrittenOnlyInTheSharedFile()
        {
            string root = TestEnvironment.RepoRoot();
            string shared = Path.Combine(root, "Agent", "POps.Shared", "FirewallRules.cs");
            string[] names = FirewallRules.Groups.Concat(new[] { FirewallRules.LegacyIsolationRules }).ToArray();
            var copies = new List<string>();
            foreach (string dir in new[] { Path.Combine(root, "Agent", "POps.Agent"), Path.Combine(root, "Agent", "POps.Shared"), Path.Combine(root, "Installer", "agent", "CustomActions") })
                foreach (string file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
                {
                    string relative = file.Substring(root.Length).TrimStart('\\');
                    if (relative.Split('\\').Any(p => p == "bin" || p == "obj") || string.Equals(file, shared, StringComparison.OrdinalIgnoreCase)) continue;
                    string[] lines = File.ReadAllLines(file);
                    for (int i = 0; i < lines.Length; i++)
                    {
                        string line = lines[i].TrimStart();
                        if (line.StartsWith("//", StringComparison.Ordinal)) continue;
                        foreach (string name in names.Where(n => lines[i].Contains(n, StringComparison.Ordinal)))
                            copies.Add($"{relative}:{i + 1}: {name}");
                    }
                }
            Assert.True(File.Exists(shared), shared);
            Assert.True(copies.Count == 0, "Kural adı FirewallRules dışında yazılmış; sabiti kullanın:" + Environment.NewLine + string.Join(Environment.NewLine, copies));
        }

        // Kaldırmanın okuduğu dosyalar ve alanlar ajanın yazdıklarıdır (Worker'ın kurduğu parçalar ve onların yazdığı JSON)
        [Fact]
        public async Task TheAgentWritesTheStateTheUninstallReads()
        {
            const string profiles = "[{\"Name\":\"Domain\",\"Enabled\":\"True\"},{\"Name\":\"Private\",\"Enabled\":\"False\"},{\"Name\":\"Public\",\"Enabled\":\"True\"}]";
            using var agent = AgentHarness.Create("fw-state", machine: new MachineSeams
            {
                Kiosk = new FakeKioskRegistry(),
                FirewallScripts = _ => Task.FromResult((0, profiles)),
                ExamResolver = _ => Task.FromResult(new[] { IPAddress.Parse("198.51.100.7") }),
            });
            using (var worker = new Worker(NullLogger<Worker>.Instance, agent.Context))
            {
                Assert.Contains(Path.GetFileName(worker.Isolation.StatePath), FirewallRules.StateFiles);
                Assert.Contains(Path.GetFileName(worker.Exam.StatePath), FirewallRules.StateFiles);
                Assert.Contains(Path.GetFileName(worker.Quarantine.LockPath), FirewallRules.StateFiles);
                Assert.Equal(agent.Paths.SecureDir, Path.GetDirectoryName(worker.Isolation.StatePath));
                Assert.Equal(agent.Paths.SecureDir, Path.GetDirectoryName(worker.Exam.StatePath));
                Assert.Equal(agent.Paths.SecureDir, Path.GetDirectoryName(worker.Quarantine.LockPath));

                Assert.True(await worker.Isolation.EnableAsync("https://203.0.113.10"));
                using (JsonDocument isolation = JsonDocument.Parse(File.ReadAllText(worker.Isolation.StatePath)))
                {
                    JsonElement[] previous = isolation.RootElement.GetProperty(FirewallRules.IsolationProfilesKey).EnumerateArray().ToArray();
                    Assert.Equal(new[] { "Domain", "Private", "Public" }, previous.Select(p => p.GetProperty("Name").GetString()));
                    Assert.Equal(new[] { "True", "False", "True" }, previous.Select(p => p.GetProperty("Enabled").GetString()));
                }

                string command = "{\"action\":\"exam_mode\",\"enabled\":true,\"allow\":[\"10.0.0.5\"]}";
                Assert.True(ExamMode.TryParse(JsonDocument.Parse(command).RootElement.Clone(), DateTimeOffset.UtcNow, out ExamSettings settings, out string error), error);
                Assert.True(await worker.Exam.EnableAsync(settings, "https://203.0.113.10"));
                using (JsonDocument exam = JsonDocument.Parse(File.ReadAllText(worker.Exam.StatePath)))
                    Assert.Equal(new[] { "Private" }, exam.RootElement.GetProperty(FirewallRules.ExamProfilesKey).EnumerateArray().Select(p => p.GetString()));
            }
        }
    }
}
