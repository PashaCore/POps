using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace POps.Shared
{
    // Log saklama. C:\POpsLogs'taki günlük loglar (POps_<tarih>.log) ve updater'ın msiexec logları (msi-*.log) hiç
    // silinmiyordu. 30 günden eski olanlar silinir; kalanlar 200 MB'ı aşarsa en eskiden başlanarak silinir. O anda
    // yazılan log (bugünün) hiç silinmez. Yalnızca bu adlardaki düz dosyalara dokunulur: bağlantı (reparse point)
    // atlanır, alt klasörlere inilmez, klasörün ACL'i (POpsHelpers.SecureLogDirectory) değişmez.
    public static class LogRetention
    {
        public static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);
        public const long MaxTotalBytes = 200L * 1024 * 1024;
        public static readonly string[] MachinePatterns = { "POps_*.log", "msi-*.log" };

        public readonly struct LogFile
        {
            public LogFile(string path, long size, DateTime lastWriteUtc)
            {
                Path = path;
                Size = size;
                LastWriteUtc = lastWriteUtc;
            }

            public string Path { get; }
            public long Size { get; }
            public DateTime LastWriteUtc { get; }
        }

        // Silinecekler: önce yaşı geçenler, sonra toplam sınırın altına inene kadar en eskiler. keepPath hiç seçilmez
        // ama boyutu toplama sayılır.
        public static List<string> Select(IEnumerable<LogFile> files, DateTime nowUtc, string keepPath, TimeSpan maxAge, long maxTotalBytes)
        {
            List<LogFile> all = files.ToList();
            long total = all.Sum(f => f.Size);
            var candidates = all
                .Where(f => !string.Equals(f.Path, keepPath, StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f.LastWriteUtc)
                .ToList();
            var delete = new List<string>();
            foreach (LogFile file in candidates.Where(f => nowUtc - f.LastWriteUtc > maxAge))
            {
                delete.Add(file.Path);
                total -= file.Size;
            }
            foreach (LogFile file in candidates)
            {
                if (total <= maxTotalBytes) break;
                if (delete.Contains(file.Path)) continue;
                delete.Add(file.Path);
                total -= file.Size;
            }
            return delete;
        }

        // Dönen: silinen dosya sayısı ve boşalan bayt. Silinemeyen (ör. açık) dosya atlanır.
        public static (int Deleted, long FreedBytes) Apply(string directory, IEnumerable<string> patterns, DateTime nowUtc, string keepPath,
            TimeSpan? maxAge = null, long? maxTotalBytes = null)
        {
            if (!Directory.Exists(directory)) return (0, 0);
            var files = new Dictionary<string, LogFile>(StringComparer.OrdinalIgnoreCase);
            foreach (string pattern in patterns)
                foreach (string path in Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly))
                {
                    var info = new FileInfo(path);
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    files[path] = new LogFile(path, info.Length, info.LastWriteTimeUtc);
                }

            int deleted = 0;
            long freed = 0;
            foreach (string path in Select(files.Values, nowUtc, keepPath, maxAge ?? MaxAge, maxTotalBytes ?? MaxTotalBytes))
            {
                try
                {
                    File.Delete(path);
                    deleted++;
                    freed += files[path].Size;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return (deleted, freed);
        }
    }
}
