using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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

        // Satır sonu beklemeden okunan parça (CommandRunner): sığmayan kısım atılır, yalnızca sayılır
        public void Append(char[] buffer, int index, int count)
        {
            if (buffer == null || count <= 0) return;
            lock (_gate)
            {
                int room = _maxChars - _text.Length;
                int take = Math.Clamp(room, 0, count);
                if (take > 0) _text.Append(buffer, index, take);
                DroppedChars += count - take;
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
        // Terminal yeteneği kapalı: görev çalıştırılmadı (Worker gönderir; eski sunucu da Failed sayar)
        public const int ExitDenied = -5;
        // Aynı görev kimliği zaten çalışıyor: ikinci çalıştırma başlatılmaz (Worker bu sonucu sunucuya göndermez)
        public const int ExitDuplicate = -6;

        // Çıktı satır sonu beklemeden bu boyutta parçalarla okunur
        internal const int ReadChunkChars = 8192;
        // İşlem bittikten sonra çıktı akışlarının kapanması için en çok beklenen süre (akışı açık tutan alt süreç sonucu bekletmesin)
        internal static TimeSpan StreamDrainTimeout { get; set; } = TimeSpan.FromSeconds(10);

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

        public bool IsRunning(int taskId) => _running.ContainsKey(taskId);

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

        // Görev kimliği ÇAĞRI anında ayrılır (TryAdd): aynı kimlik zaten çalışıyorsa ikinci işlem başlatılmaz, ExitDuplicate döner
        public Task<CommandExecutionResult> RunAsync(int taskId, string command, CancellationToken serviceStopping)
        {
            var cancel = new CancellationTokenSource();
            if (!_running.TryAdd(taskId, cancel))
            {
                cancel.Dispose();
                return Task.FromResult(new CommandExecutionResult(
                    $"[YİNELENEN]: Görev {taskId} zaten çalışıyor; ikinci kez başlatılmadı.", ExitDuplicate, TimeSpan.Zero));
            }
            return RunReservedAsync(taskId, command, cancel, serviceStopping);
        }

        private async Task<CommandExecutionResult> RunReservedAsync(int taskId, string command, CancellationTokenSource cancel, CancellationToken serviceStopping)
        {
            var stopwatch = Stopwatch.StartNew();
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
                process.Start();
                // Satır okuyucu (BeginOutputReadLine) satır sonu içermeyen dev bir çıktıyı sınırdan önce bellekte biriktirirdi:
                // akışlar sabit parçalarla okunur, sınır dolunca okuma sürer ama atılır (boru tıkanıp işlem asılı kalmaz)
                Task pumps = Task.WhenAll(PumpAsync(process.StandardOutput, stdout), PumpAsync(process.StandardError, stderr));

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
                    await DrainAsync(pumps, taskId);
                    (string reason, int code) = cancel.IsCancellationRequested
                        ? ("[İPTAL EDİLDİ]: Görev panelden iptal edildi; işlem sonlandırıldı.", ExitCancelled)
                        : serviceStopping.IsCancellationRequested
                            ? ("[DURDURULDU]: POps Agent servisi durduğu için işlem sonlandırıldı.", ExitServiceStopping)
                            : ($"[HATA]: İşlem {_maxDuration.TotalMinutes:0} dakikadan uzun sürdüğü için zorla sonlandırıldı.", ExitTimeout);
                    string partial = Normalize(stdout.ToString());
                    return new CommandExecutionResult(string.IsNullOrEmpty(partial) ? reason : reason + "\n[ÇIKTI]:\n" + partial,
                        code, stopwatch.Elapsed);
                }

                await DrainAsync(pumps, taskId);
                int exitCode = process.ExitCode;
                string outText = Normalize(stdout.ToString());
                string errText = Normalize(stderr.ToString());
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
                // Yalnızca bu çalıştırmanın kaydı kaldırılır
                _running.TryRemove(new KeyValuePair<int, CancellationTokenSource>(taskId, cancel));
                cancel.Dispose();
                stopwatch.Stop();
                if (tempBatPath != null) try { File.Delete(tempBatPath); } catch { }
            }
        }

        private static async Task PumpAsync(StreamReader reader, BoundedOutput sink)
        {
            char[] buffer = new char[ReadChunkChars];
            try
            {
                int read;
                while ((read = await reader.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    sink.Append(buffer, 0, read);
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is InvalidOperationException) { }
        }

        // İşlem bitti: akışların sonuna kadar okunması beklenir, ama akışı açık tutan bir alt süreç sonucu bekletemez
        private static async Task DrainAsync(Task pumps, int taskId)
        {
            try { await pumps.WaitAsync(StreamDrainTimeout); }
            catch (TimeoutException)
            {
                POpsHelpers.Log("AGENT", $"Görev {taskId}: işlem bitti ama çıktı akışı {StreamDrainTimeout.TotalSeconds:0} sn içinde kapanmadı (açık kalan bir alt süreç); okunan çıktıyla devam ediliyor.", true);
            }
        }

        // Parçalı okumada satır sonları olduğu gibi gelir; sunucuya eskisi gibi \n ile gider
        private static string Normalize(string text) => text.Replace("\r\n", "\n").Trim();
    }
}
