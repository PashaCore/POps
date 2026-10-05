using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
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
}
