using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

#nullable disable

namespace POpsAgent
{
    // Güncelleme paketinin BITS ile indirilmesi (Windows'un BitsTransfer modülü). BITS ağ kesintisinde ve servis
    // yeniden başladığında kaldığı yerden sürer (HTTP Range); iş, paketin SHA-256'sından türeyen adla bulunur, bir
    // sonraki emir aynı işi sürdürür. İmzalı manifest'in boyut ve SHA-256 denetimi değişmez (bkz. AgentUpdate).
    // BITS kullanılamazsa (modül/hizmet yok, sertifika kurumun özel CA'sıyla ve makine deposunda değil, sunucu
    // Range desteklemiyor...) çağıran HttpClient'la indirir. Değerler betiğe yalnızca tek tırnaklı, kaçışlı
    // değişmez olarak girer.
    [SupportedOSPlatform("windows")]
    public static class BitsDownload
    {
        public enum Outcome { Done, Pending, Unavailable }

        public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(15);
        public const string JobPrefix = "POps update ";

        // Testler kapatır ya da sahte çalıştırıcı verir
        internal static bool Enabled { get; set; } = true;
        internal static Func<string, TimeSpan, Task<(int Exit, string Output)>> Runner { get; set; } = RunPowerShellAsync;

        public static string JobName(string sha256) => string.Concat(JobPrefix, sha256.AsSpan(0, 16));

        // Pending: BITS işi süre içinde bitmedi, iş durmaz (sonraki emir sürdürür). Unavailable: eski yola düşülmeli.
        public static async Task<Outcome> DownloadAsync(string url, string destination, string sha256)
        {
            if (!Enabled) return Outcome.Unavailable;
            try
            {
                (int exit, string output) = await Runner(BuildScript(url, destination, JobName(sha256), Timeout), Timeout + TimeSpan.FromMinutes(2));
                string last = LastLine(output);
                if (exit == 0 && File.Exists(destination)) return Outcome.Done;
                if (exit == 3)
                {
                    POpsHelpers.Log("UPDATE", $"BITS indirmesi {Timeout.TotalMinutes:0} dk içinde bitmedi; iş sürüyor, bir sonraki güncelleme emri kaldığı yerden devam eder.", true);
                    return Outcome.Pending;
                }
                POpsHelpers.Log("UPDATE", $"BITS kullanılamadı (çıkış {exit}: {LogText.Safe(last, 200)}); doğrudan indiriliyor.", true);
                return Outcome.Unavailable;
            }
            catch (Exception ex)
            {
                POpsHelpers.Log("UPDATE", $"BITS kullanılamadı ({ex.Message}); doğrudan indiriliyor.", true);
                return Outcome.Unavailable;
            }
        }

        // Çıkış: 0 tamam, 2 BITS hatası (iş silinir), 3 süre doldu (iş kalır), diğerleri betik/modül hatası
        internal static string BuildScript(string url, string destination, string jobName, TimeSpan timeout) => $@"$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Import-Module BitsTransfer
$name = {Quote(jobName)}
$source = {Quote(url)}
$target = {Quote(destination)}
$job = Get-BitsTransfer -Name $name -ErrorAction SilentlyContinue | Select-Object -First 1
if ($job -and (@($job.FileList)[0].RemoteName -ne $source -or @($job.FileList)[0].LocalName -ne $target)) {{ $job | Remove-BitsTransfer; $job = $null }}
if (-not $job) {{
    if (Test-Path -LiteralPath $target) {{ Remove-Item -LiteralPath $target -Force }}
    $job = Start-BitsTransfer -Asynchronous -Source $source -Destination $target -DisplayName $name -Priority Foreground -RetryInterval 60 -RetryTimeout 600
}}
$deadline = (Get-Date).AddSeconds({(int)timeout.TotalSeconds})
while ($true) {{
    switch ($job.JobState) {{
        'Transferred' {{ Complete-BitsTransfer -BitsJob $job; 'OK'; exit 0 }}
        'Error' {{ $message = $job.ErrorDescription; Remove-BitsTransfer -BitsJob $job; ""BITS: $message""; exit 2 }}
        'Suspended' {{ Resume-BitsTransfer -BitsJob $job -Asynchronous | Out-Null }}
    }}
    if ((Get-Date) -gt $deadline) {{ 'BITS: süre doldu'; exit 3 }}
    Start-Sleep -Seconds 2
}}
";

        // PowerShell tek tırnaklı değişmez. PowerShell dizgiyi ' yanında ‘ ’ ‚ ‛ ile de kapatır: hepsi ikilenir
        internal static string Quote(string value)
        {
            var sb = new StringBuilder("'");
            foreach (char c in value ?? "")
            {
                if (c is '\'' or '\u2018' or '\u2019' or '\u201A' or '\u201B') sb.Append(c);
                sb.Append(c);
            }
            return sb.Append('\'').ToString();
        }

        private static string LastLine(string output)
        {
            string[] lines = (output ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return lines.Length == 0 ? "" : lines[^1];
        }

        private static async Task<(int Exit, string Output)> RunPowerShellAsync(string script, TimeSpan timeout)
        {
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
                psi.ArgumentList.Add(arg);
            using Process p = Process.Start(psi) ?? throw new InvalidOperationException("PowerShell başlatılamadı");
            Task<string> stdout = p.StandardOutput.ReadToEndAsync();
            Task<string> stderr = p.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(timeout);
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                try { p.Kill(true); } catch (InvalidOperationException) { }
                return (-1, "PowerShell süresinde bitmedi");
            }
            return (p.ExitCode, ((await stdout) + (await stderr)).Trim());
        }
    }
}
