using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Text.Json;
using POpsAgent;
using Xunit;

namespace POps.Tests.Agent
{
    // Yalnızca adres aralığı hesabı ve betik metni test edilir; betik asla çalıştırılmaz (güvenlik duvarını değiştirir)
    public class NetworkIsolationTests : TestBase
    {
        private static readonly IPAddress[] Allowed =
        {
            IPAddress.Parse("203.0.113.10"), IPAddress.Parse("2001:db8::5"), IPAddress.Parse("192.0.2.53"),
            IPAddress.Parse("fe80::1%12"), IPAddress.Broadcast, IPAddress.Parse("::ffff:198.51.100.7"),
        };

        private static List<(BigInteger Start, BigInteger End, bool V6)> BlockedRanges()
        {
            return NetworkIsolation.BlockedRanges(NetworkIsolation.AllowedRanges(Allowed)).Select(r =>
            {
                string[] parts = r.Split('-');
                IPAddress a = IPAddress.Parse(parts[0]), b = IPAddress.Parse(parts.Length > 1 ? parts[1] : parts[0]);
                return (Big(a), Big(b), a.AddressFamily == AddressFamily.InterNetworkV6);
            }).ToList();
        }

        private static BigInteger Big(IPAddress a) => new BigInteger(a.GetAddressBytes(), isUnsigned: true, isBigEndian: true);

        private static bool IsBlocked(string ip)
        {
            IPAddress a = IPAddress.Parse(ip);
            BigInteger v = Big(a);
            bool v6 = a.AddressFamily == AddressFamily.InterNetworkV6;
            return BlockedRanges().Any(r => r.V6 == v6 && r.Start <= v && v <= r.End);
        }

        [Theory]
        [InlineData("203.0.113.10")]      // sunucu
        [InlineData("2001:db8::5")]       // sunucu (IPv6)
        [InlineData("198.51.100.7")]      // IPv4-mapped olarak verilen sunucu
        [InlineData("192.0.2.53")]        // DNS
        [InlineData("255.255.255.255")]   // DHCP yayını
        [InlineData("127.0.0.1")]
        [InlineData("::1")]
        [InlineData("fe80::abcd")]        // komşu keşfi
        [InlineData("ff02::1:2")]         // DHCPv6 / çoklu yayın
        public void RequiredAddresses_StayReachable(string ip) => Assert.False(IsBlocked(ip));

        [Theory]
        [InlineData("0.0.0.0")]
        [InlineData("8.8.8.8")]
        [InlineData("203.0.113.9")]
        [InlineData("203.0.113.11")]
        [InlineData("255.255.255.254")]
        [InlineData("::")]
        [InlineData("2001:db8::4")]
        [InlineData("2001:db8::6")]
        [InlineData("2606:4700::1111")]
        [InlineData("fec0::1")]
        public void EverythingElse_IsBlocked(string ip) => Assert.True(IsBlocked(ip));

        [Fact]
        public void Ranges_DoNotOverlap()
        {
            foreach (var family in BlockedRanges().GroupBy(r => r.V6))
            {
                var sorted = family.OrderBy(r => r.Start).ToList();
                for (int i = 1; i < sorted.Count; i++) Assert.True(sorted[i - 1].End < sorted[i].Start);
            }
        }

        [Fact]
        public void EnableScript_UsesOnlyBlockRules_AndTurnsTheFirewallOn()
        {
            string script = NetworkIsolation.BuildEnableScript(NetworkIsolation.AllowedRanges(Allowed));
            Assert.Contains("-Action Block", script);
            Assert.DoesNotContain("-Action Allow", script);
            Assert.Contains("Set-NetFirewallProfile -Profile Domain,Private,Public -Enabled True", script);
            Assert.DoesNotContain("'203.0.113.10'", script);
            Assert.Contains("'203.0.113.11-", script);
        }

        [Fact]
        public void DisableScript_RestoresOnlyKnownProfiles()
        {
            string script = NetworkIsolation.BuildDisableScript(new[] { "Private", "Evil'; Remove-Item C:\\ -Recurse #", "Public" });
            Assert.Contains("Set-NetFirewallProfile -Profile Private -Enabled False", script);
            Assert.Contains("Set-NetFirewallProfile -Profile Public -Enabled False", script);
            Assert.DoesNotContain("Evil", script);
            Assert.DoesNotContain("Remove-Item", script);
        }

        [Fact]
        public void Scripts_ParseWithoutErrors()
        {
            string dir = TestEnvironment.NewDir("iso");
            File.WriteAllText(Path.Combine(dir, "enable.ps1"), NetworkIsolation.BuildEnableScript(NetworkIsolation.AllowedRanges(Allowed)));
            File.WriteAllText(Path.Combine(dir, "disable.ps1"), NetworkIsolation.BuildDisableScript(new[] { "Public" }));
            // Sınav modu aynı betikleri kendi kural grubuyla kullanır
            File.WriteAllText(Path.Combine(dir, "exam-enable.ps1"), NetworkIsolation.BuildEnableScript(NetworkIsolation.AllowedRanges(Allowed), NetworkIsolation.ExamRuleGroup));
            File.WriteAllText(Path.Combine(dir, "exam-disable.ps1"), NetworkIsolation.BuildDisableScript(new[] { "Public", "Private" }, NetworkIsolation.ExamRuleGroup));
            foreach (string name in new[] { "enable.ps1", "disable.ps1", "exam-enable.ps1", "exam-disable.ps1" })
            {
                // Yalnızca ayrıştırılır, çalıştırılmaz
                var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
                foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-Command", $"$e = $null; [void][System.Management.Automation.Language.Parser]::ParseFile('{Path.Combine(dir, name)}', [ref]$null, [ref]$e); $e.Count" })
                    psi.ArgumentList.Add(arg);
                using Process p = Process.Start(psi);
                string output = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit();
                Assert.Equal("0", output);
            }
        }

        [Fact]
        public void PreviousFirewallState_IsReadBack()
        {
            SecureStore.Dir = TestEnvironment.NewDir("iso-state");
            File.WriteAllText(NetworkIsolation.StatePath, "{\"previous_profiles\":[{\"Name\":\"Domain\",\"Enabled\":\"True\"},{\"Name\":\"Private\",\"Enabled\":\"False\"}],\"since\":1}");
            Assert.Equal(new[] { "Private" }, NetworkIsolation.ReadPreviouslyDisabledProfiles());
        }
    }

    // A3: karantinada sunucu adresi değişirse izin listesi yenilenir; önceki profil durumu korunur
    public class IsolationRefreshTests : TestBase
    {
        private static IPAddress Ip(string s) => IPAddress.Parse(s);

        [Fact]
        public void SameAddresses_IgnoresOrderDuplicatesMappingAndScope()
        {
            Assert.True(NetworkIsolation.SameAddresses(new[] { Ip("203.0.113.10"), Ip("2001:db8::1") }, new[] { "2001:db8::1", "203.0.113.10" }));
            Assert.True(NetworkIsolation.SameAddresses(new[] { Ip("::ffff:203.0.113.10"), Ip("203.0.113.10") }, new[] { "203.0.113.10" }));
            Assert.True(NetworkIsolation.SameAddresses(new[] { Ip("fe80::1%12") }, new[] { "fe80::1" }));
            Assert.False(NetworkIsolation.SameAddresses(new[] { Ip("203.0.113.11") }, new[] { "203.0.113.10" }));
            Assert.False(NetworkIsolation.SameAddresses(new[] { Ip("203.0.113.10"), Ip("203.0.113.11") }, new[] { "203.0.113.10" }));
            Assert.False(NetworkIsolation.SameAddresses(new[] { Ip("203.0.113.10") }, new[] { "çöp" }));
        }

        [Fact]
        public void Merge_KeepsPreviousProfilesAndSince_ReplacesOnlyServerAddresses()
        {
            const string first = "{\"previous_profiles\":[{\"Name\":\"Domain\",\"Enabled\":\"True\"},{\"Name\":\"Private\",\"Enabled\":\"False\"}],\"since\":1700000000}";
            string withServer = NetworkIsolation.MergeServerAddresses(first, new[] { Ip("203.0.113.10") });
            string refreshed = NetworkIsolation.MergeServerAddresses(withServer, new[] { Ip("198.51.100.7"), Ip("198.51.100.7") });

            using JsonDocument doc = JsonDocument.Parse(refreshed);
            Assert.Equal(1700000000, doc.RootElement.GetProperty("since").GetInt64());
            Assert.Equal("False", doc.RootElement.GetProperty("previous_profiles")[1].GetProperty("Enabled").GetString());
            Assert.Equal(new[] { "198.51.100.7" }, NetworkIsolation.ReadServerAddresses(refreshed));
        }

        [Fact]
        public void Refresh_DoesNotTouchTheProfilesThatUnlockRestores()
        {
            SecureStore.Dir = TestEnvironment.NewDir("iso-refresh");
            File.WriteAllText(NetworkIsolation.StatePath, "{\"previous_profiles\":[{\"Name\":\"Public\",\"Enabled\":\"False\"}],\"since\":5}");
            string merged = NetworkIsolation.MergeServerAddresses(File.ReadAllText(NetworkIsolation.StatePath), new[] { Ip("203.0.113.99") });
            File.WriteAllText(NetworkIsolation.StatePath, merged);
            Assert.Equal(new[] { "Public" }, NetworkIsolation.ReadPreviouslyDisabledProfiles());
            Assert.True(NetworkIsolation.IsActive);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("{\"previous_profiles\":[]}")]
        [InlineData("{bozuk")]
        [InlineData("{\"server_addresses\":\"203.0.113.1\"}")]
        public void UnknownServerAddresses_AreNull(string state) => Assert.Null(NetworkIsolation.ReadServerAddresses(state));

        [Fact]
        public void Merge_OfAnUnreadableState_StillRecordsTheServer() =>
            Assert.Equal(new[] { "203.0.113.10" }, NetworkIsolation.ReadServerAddresses(NetworkIsolation.MergeServerAddresses("{bozuk", new[] { Ip("203.0.113.10") })));

        [Fact]
        public void NormalizeAddresses_SortedAndDistinct() =>
            Assert.Equal(new[] { "10.0.0.2", "10.0.0.9", "2001:db8::5" },
                NetworkIsolation.NormalizeAddresses(new[] { Ip("2001:db8::5"), Ip("10.0.0.9"), Ip("::ffff:10.0.0.2"), Ip("10.0.0.9"), null }));

        [Fact]
        public void EnableScript_AddsNewRulesBeforeRemovingOldOnes()
        {
            string script = NetworkIsolation.BuildEnableScript(NetworkIsolation.AllowedRanges(new[] { Ip("203.0.113.10") }));
            int capture = script.IndexOf("$old = @(Get-NetFirewallRule -Group $group", StringComparison.Ordinal);
            int add = script.IndexOf("New-NetFirewallRule", StringComparison.Ordinal);
            int remove = script.IndexOf("$old | Remove-NetFirewallRule", StringComparison.Ordinal);
            Assert.True(capture >= 0 && capture < add && add < remove, script);
        }
    }
}
