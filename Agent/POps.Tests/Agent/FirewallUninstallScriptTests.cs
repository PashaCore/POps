using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using POps.Shared;
using Xunit;

namespace POps.Tests.Agent
{
    // MSI kaldırmasının güvenlik duvarı betiği (FirewallRules.BuildUninstallScript) gerçek Windows PowerShell'de çalıştırılır, ama
    // güvenlik duvarı cmdlet'leri (Get-NetFirewallRule, Remove-NetFirewallRule, Set-NetFirewallProfile) bellekte bir kural listesi
    // tutan sahte işlevlerdir: hangi kuralları sildiği, hangilerine dokunmadığı, hangi profilleri kapattığı ve sorun olunca çıkış
    // kodu. Makinenin güvenlik duvarına dokunulmaz: modüllerin kendiliğinden yüklenmesi kapalıdır ($PSModuleAutoLoadingPreference
    // = 'None': NetSecurity hiç yüklenmez, işlevler cmdlet'lerden önce gelir; Get-Content gibi cmdlet'ler de yüklenmediği için
    // sarmalayıcı dosyaları .NET ile okuyup yazar) ve sarmalayıcı, üç adın da sahte işleve çözüldüğünü ve NetSecurity'nin yüklü
    // olmadığını doğrulamadan betiği çalıştırmaz (çıkış 99).
    // Machine: powershell.exe başlatır.
    [Collection(MachineCollection.Name)]
    public class FirewallUninstallScriptTests : TestBase
    {
        private const int Refused = 99;

        private static string Quote(string value) => "'" + value.Replace("'", "''") + "'";

        private static string Wrapper(string script, string calls, string mode) => $@"$ErrorActionPreference = 'Stop'
$PSModuleAutoLoadingPreference = 'None'
$global:mode = {Quote(mode)}
$global:calls = {Quote(calls)}
$global:rules = @(
    [pscustomobject]@{{ DisplayName = 'POps Isolation - Outbound'; Group = 'POps Isolation' }},
    [pscustomobject]@{{ DisplayName = 'POps Isolation - Inbound'; Group = 'POps Isolation' }},
    [pscustomobject]@{{ DisplayName = 'POps Exam - Inbound'; Group = 'POps Exam' }},
    [pscustomobject]@{{ DisplayName = 'POps Peer Cache (TCP 8817)'; Group = 'POps Peer Cache' }},
    [pscustomobject]@{{ DisplayName = 'POps_Isolation_BlockOut'; Group = $null }},
    [pscustomobject]@{{ DisplayName = 'Core Networking - DNS (UDP-Out)'; Group = '@FirewallAPI.dll,-25000' }},
    [pscustomobject]@{{ DisplayName = 'POps server (school rule)'; Group = 'School' }}
)
function global:Get-NetFirewallRule {{
    if ($global:mode -eq 'query-fails') {{ throw 'firewall not responding' }}
    $global:rules
}}
function global:Remove-NetFirewallRule {{
    param([Parameter(ValueFromPipeline = $true)] $InputObject)
    process {{
        $target = $InputObject.DisplayName
        if ($global:mode -eq 'stuck' -and $target -eq 'POps Exam - Inbound') {{ throw 'access denied' }}
        $global:rules = @($global:rules | Where-Object {{ $_.DisplayName -ne $target }})
        [System.IO.File]::AppendAllText($global:calls, 'removed ' + $target + [Environment]::NewLine)
    }}
}}
function global:Set-NetFirewallProfile {{
    param([Alias('Profile')] [string[]] $Name, [string] $Enabled)
    [System.IO.File]::AppendAllText($global:calls, 'profile ' + ($Name -join ',') + ' ' + $Enabled + [Environment]::NewLine)
}}
foreach ($name in 'Get-NetFirewallRule', 'Remove-NetFirewallRule', 'Set-NetFirewallProfile') {{
    $found = @(Get-Command $name -ListImported)
    if ($found.Count -ne 1 -or $found[0].CommandType -ne 'Function' -or @(Get-Command $name).Count -ne 1 -or (Get-Command $name).CommandType -ne 'Function') {{ 'REFUSED ' + $name; exit {Refused} }}
}}
if (Get-Module NetSecurity) {{ 'REFUSED NetSecurity'; exit {Refused} }}
& ([scriptblock]::Create([System.IO.File]::ReadAllText({Quote(script)})))
";

        private static (int Exit, string Output, List<string> Calls) Run(string mode, params string[] profiles)
        {
            string dir = TestEnvironment.NewDir("fw-uninstall");
            string script = Path.Combine(dir, "uninstall.ps1");
            string calls = Path.Combine(dir, "calls.log");
            File.WriteAllText(script, FirewallRules.BuildUninstallScript(profiles));
            File.WriteAllText(calls, "");
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(Wrapper(script, calls, mode))) })
                psi.ArgumentList.Add(arg);
            using Process p = Process.Start(psi);
            Task<string> stdout = p.StandardOutput.ReadToEndAsync();
            Task<string> stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(120_000))
            {
                p.Kill(true);
                Assert.Fail("PowerShell 2 dakikada bitmedi");
            }
            string output = (stdout.Result + stderr.Result).Trim();
            Assert.True(p.ExitCode != Refused, output);
            return (p.ExitCode, output, File.ReadAllLines(calls).Where(l => l.Length > 0).ToList());
        }

        private static readonly string[] PopsRules =
        {
            "POps Isolation - Outbound", "POps Isolation - Inbound", "POps Exam - Inbound", "POps Peer Cache (TCP 8817)", "POps_Isolation_BlockOut",
        };

        [Fact]
        public void RemovesEveryPopsRule_KeepsOtherRules_AndSwitchesOffTheProfilesThatWereOff()
        {
            var (exit, output, calls) = Run("ok", "Public", "Private");
            Assert.True(exit == 0, output);
            Assert.Equal("OK 5", output.Split('\n').Last().Trim());
            Assert.Equal(PopsRules.Select(r => "removed " + r), calls.Where(c => c.StartsWith("removed ", StringComparison.Ordinal)));
            // Başkasının kuralı (adında POps geçse de) ve Windows'un kuralları kalır
            Assert.DoesNotContain(calls, c => c.Contains("school rule", StringComparison.Ordinal) || c.Contains("Core Networking", StringComparison.Ordinal));
            Assert.Equal(new[] { "profile Public False", "profile Private False" }, calls.Where(c => c.StartsWith("profile ", StringComparison.Ordinal)));
        }

        [Fact]
        public void ARuleThatCannotBeRemoved_FailsWithExitCode2_ButTheRestIsCleanedUp()
        {
            var (exit, output, calls) = Run("stuck", "Public");
            Assert.True(exit == 2, output);
            Assert.Contains("POps Exam - Inbound: access denied", output);
            Assert.Contains("left: POps Exam - Inbound", output);
            Assert.Equal(PopsRules.Where(r => r != "POps Exam - Inbound").Select(r => "removed " + r), calls.Where(c => c.StartsWith("removed ", StringComparison.Ordinal)));
            Assert.Contains("profile Public False", calls);
        }

        // Güvenlik duvarı sorgulanamazsa boş sonuç "temiz" sayılmaz
        [Fact]
        public void AFirewallThatCannotBeQueried_IsNotReportedAsClean()
        {
            var (exit, output, calls) = Run("query-fails", "Public");
            Assert.True(exit == 2, output);
            Assert.Contains("rules: firewall not responding", output);
            Assert.Contains("check: firewall not responding", output);
            Assert.DoesNotContain(calls, c => c.StartsWith("removed ", StringComparison.Ordinal));
            Assert.Contains("profile Public False", calls);
        }
    }
}
