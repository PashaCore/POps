using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

#nullable disable

namespace POpsAgent
{
    // Komut çıktısını OKURKEN sınırlar (F10): sınırı aşan kısım bellekte tutulmaz, yalnızca sayılır. Eskiden bütün
    // çıktı biriktirilip sonda kısaltılıyordu; durmadan yazan bir betik servisin belleğini tüketebilirdi.
    public sealed class BoundedOutput
    {
        private readonly StringBuilder _text = new StringBuilder();
        private readonly object _gate = new object();
        private readonly int _maxChars;

        public BoundedOutput(int maxChars) => _maxChars = Math.Max(0, maxChars);

        public long DroppedChars { get; private set; }

        public void AppendLine(string line)
        {
            if (line == null) return;
            lock (_gate)
            {
                int needed = line.Length + 1;
                int room = _maxChars - _text.Length;
                if (room >= needed)
                {
                    _text.Append(line).Append('\n');
                    return;
                }
                int take = Math.Clamp(room, 0, line.Length);
                if (take > 0) _text.Append(line, 0, take);
                DroppedChars += needed - take;
            }
        }

        public override string ToString()
        {
            lock (_gate)
                return DroppedChars > 0
                    ? _text.ToString().TrimEnd() + $"\n[ÇIKTI KISALTILDI: {DroppedChars} karakter atıldı]"
                    : _text.ToString();
        }
    }

    // Uzaktan komutları çalıştırır. İşlem (alt süreçleriyle) üç durumda sonlandırılır: 30 dakikalık süre doldu,
    // görev panelden iptal edildi (cancel_task) ya da servis duruyor. Çıkış kodu sonuçla birlikte sunucuya gider.
    public sealed class CommandRunner
    {
        public const int ExitTimeout = -1;
        public const int ExitCancelled = -2;
        public const int ExitAgentError = -3;
        public const int ExitServiceStopping = -4;

        private readonly ConcurrentDictionary<int, CancellationTokenSource> _running = new ConcurrentDictionary<int, CancellationTokenSource>();
        private readonly string _shell;
        private readonly Func<string, string> _arguments;
        private readonly TimeSpan _maxDuration;

        // shell/arguments/maxDuration yalnızca testler içindir; ajan cmd.exe ve bir .bat dosyası kullanır
        public CommandRunner(string shell = null, Func<string, string> arguments = null, TimeSpan? maxDuration = null)
        {
            _shell = shell ?? "cmd.exe";
            _arguments = arguments ?? (bat => $"/c \"{bat}\"");
            _maxDuration = maxDuration ?? CommandExecutionPolicy.MaxDuration;
        }

        public int RunningCount => _running.Count;

        // Görev dosyası: Path.GetTempPath() altında pops_task_<32 küçük hex>.bat (Guid "N"). Görev bitince silinir; servis
        // ya da makine çökerse kalır ve içinde yönetici komutu olabilir.
        private static readonly Regex TaskFileName = new Regex(@"^pops_task_[0-9a-f]{32}\.bat$", RegexOptions.CultureInvariant);

        public static bool IsTaskFile(string fileName) => fileName != null && TaskFileName.IsMatch(fileName);

        // Açılışta, ilk görevden önce: yalnızca bu desene uyan dosyalar silinir. Silinemeyen loglanır, açılış durmaz.
        public static (int Deleted, int Failed) CleanupStaleTaskFiles(string directory = null)
        {
            directory ??= Path.GetTempPath();
            int deleted = 0, failed = 0;
            try
            {
                foreach (string path in Directory.EnumerateFiles(directory, "pops_task_*.bat", SearchOption.TopDirectoryOnly))
                {
                    if (!IsTaskFile(Path.GetFileName(path))) continue;
                    try
                    {
                        File.Delete(path);
                        deleted++;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        POpsHelpers.Log("AGENT", $"Yarım kalmış görev dosyası silinemedi ({Path.GetFileName(path)}): {ex.Message}", true);
                    }
                }
            }
            catch (Exception ex)
            {
                POpsHelpers.Log("AGENT", $"Yarım kalmış görev dosyaları taranamadı ({directory}): {ex.Message}", true);
            }
            if (deleted > 0) POpsHelpers.Log("AGENT", $"Önceki çalışmadan kalan {deleted} görev dosyası silindi ({directory}).");
            return (deleted, failed);
        }

        // Görev çalışıyorsa iptal edilir; dönen: böyle bir görev vardı mı
        public bool Cancel(int taskId)
        {
            if (!_running.TryGetValue(taskId, out CancellationTokenSource cts)) return false;
            try { cts.Cancel(); } catch (ObjectDisposedException) { return false; }
            return true;
        }

        public async Task<CommandExecutionResult> RunAsync(int taskId, string command, CancellationToken serviceStopping)
        {
            var stopwatch = Stopwatch.StartNew();
            using var cancel = new CancellationTokenSource();
            _running[taskId] = cancel;
            string tempBatPath = null;
            try
            {
                tempBatPath = Path.Combine(Path.GetTempPath(), $"pops_task_{Guid.NewGuid():N}.bat");
                await File.WriteAllTextAsync(tempBatPath, "@echo off\r\nchcp 65001 > nul\r\n" + command, new UTF8Encoding(false));

                var info = new ProcessStartInfo
                {
                    FileName = _shell,
                    Arguments = _arguments(tempBatPath),
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };
                using var process = new Process { StartInfo = info };
                var stdout = new BoundedOutput(CommandExecutionPolicy.MaxOutputChars);
                var stderr = new BoundedOutput(CommandExecutionPolicy.MaxOutputChars / 4);
                process.OutputDataReceived += (_, e) => stdout.AppendLine(e.Data);
                process.ErrorDataReceived += (_, e) => stderr.AppendLine(e.Data);
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                using var timeout = new CancellationTokenSource(_maxDuration);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancel.Token, serviceStopping);
                try
                {
                    await process.WaitForExitAsync(linked.Token);
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
                    (string reason, int code) = cancel.IsCancellationRequested
                        ? ("[İPTAL EDİLDİ]: Görev panelden iptal edildi; işlem sonlandırıldı.", ExitCancelled)
                        : serviceStopping.IsCancellationRequested
                            ? ("[DURDURULDU]: POps Agent servisi durduğu için işlem sonlandırıldı.", ExitServiceStopping)
                            : ($"[HATA]: İşlem {_maxDuration.TotalMinutes:0} dakikadan uzun sürdüğü için zorla sonlandırıldı.", ExitTimeout);
                    string partial = stdout.ToString().Trim();
                    return new CommandExecutionResult(string.IsNullOrEmpty(partial) ? reason : reason + "\n[ÇIKTI]:\n" + partial,
                        code, stopwatch.Elapsed);
                }

                int exitCode = process.ExitCode;
                string outText = stdout.ToString().Trim();
                string errText = stderr.ToString().Trim();
                string output = exitCode != 0 && !string.IsNullOrWhiteSpace(errText)
                    ? $"[ÇIKIŞ KODU: {exitCode}]\n[HATA]:\n{errText}\n[ÇIKTI]:\n{outText}"
                    : string.IsNullOrWhiteSpace(outText) ? $"Komut çalıştı (Çıkış: {exitCode}) ancak çıktı üretilmedi." : outText;
                return new CommandExecutionResult(output, exitCode, stopwatch.Elapsed);
            }
            catch (Exception ex)
            {
                return new CommandExecutionResult($"Ajan Hatası: {ex.Message}", ExitAgentError, stopwatch.Elapsed);
            }
            finally
            {
                _running.TryRemove(taskId, out _);
                stopwatch.Stop();
                if (tempBatPath != null) try { File.Delete(tempBatPath); } catch { }
            }
        }
    }
}
