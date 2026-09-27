using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

#nullable disable

namespace POpsAgent
{
    // Sunucu modeli: Backend/pops/models.py PatchUpdateItem
    public sealed class PatchUpdateItem
    {
        [JsonPropertyName("kb")] public string Kb { get; set; }
        [JsonPropertyName("title")] public string Title { get; set; }
        [JsonPropertyName("severity")] public string Severity { get; set; }
        [JsonPropertyName("categories")] public List<string> Categories { get; set; } = new List<string>();
        [JsonPropertyName("is_security")] public bool IsSecurity { get; set; }
    }

    // Sunucu modeli: Backend/pops/models.py PatchStatusInput
    public sealed class PatchStatusPayload
    {
        [JsonPropertyName("pending_count")] public int PendingCount { get; set; }
        [JsonPropertyName("pending_security")] public int PendingSecurity { get; set; }
        [JsonPropertyName("pending_critical")] public int PendingCritical { get; set; }
        [JsonPropertyName("reboot_required")] public bool RebootRequired { get; set; }
        [JsonPropertyName("last_search")] public string LastSearch { get; set; }
        [JsonPropertyName("last_install")] public string LastInstall { get; set; }
        [JsonPropertyName("updates")] public List<PatchUpdateItem> Updates { get; set; } = new List<PatchUpdateItem>();
        // null: sunucu önceki kurulum sonucunu korur (yalnızca kurulumdan sonra doldurulur)
        [JsonPropertyName("last_result")] public string LastResult { get; set; }
    }

    // Windows Update'in bulduğu, kurulmamış bir güncelleme (WUA IUpdate'ten okunan alanlar)
    public sealed class PendingUpdate
    {
        public string Kb { get; set; }
        public string Title { get; set; }
        // MSRC önem derecesi: Critical / Important / Moderate / Low, güvenlik dışı güncellemede null
        public string Severity { get; set; }
        // Kategori adları yalnızca gösterim içindir; sınıflandırma kategori kimlikleriyle yapılır
        public List<string> Categories { get; set; } = new List<string>();
        public List<string> CategoryIds { get; set; } = new List<string>();
        // Kurulumu kullanıcı girişi isteyebilir (gözetimsiz kurulmaz)
        public bool NeedsUserInput { get; set; }

        public bool IsSecurity => PatchClassifier.IsSecurity(this);
        public bool IsCritical => PatchClassifier.IsCritical(this);
    }

    public static class PatchClassifier
    {
        // Windows Update sınıflandırma kimlikleri (WSUS "UpdateClassification"). Kategori ADLARI Windows diline göre
        // değişebildiği için (bkz. F8: Türkçe Windows'ta İngilizce etiket aramak) sınıflandırma kimlikle yapılır;
        // ad yalnızca kimlik gelmediğinde yedek olarak denetlenir.
        public const string SecurityUpdatesId = "0fa1201d-4330-4fa8-8ae9-b877473b6441";
        public const string CriticalUpdatesId = "e6cf1350-c01b-414d-a61f-263d14d133b4";
        public const int MaxUpdates = 500;

        public static bool IsSecurity(PendingUpdate update) =>
            HasCategory(update, SecurityUpdatesId, "Security Updates") || !string.IsNullOrWhiteSpace(update.Severity);

        public static bool IsCritical(PendingUpdate update) =>
            string.Equals(update.Severity?.Trim(), "Critical", StringComparison.OrdinalIgnoreCase) || HasCategory(update, CriticalUpdatesId, "Critical Updates");

        private static bool HasCategory(PendingUpdate update, string id, string englishName) =>
            (update.CategoryIds ?? new List<string>()).Any(c => string.Equals(c?.Trim().Trim('{', '}'), id, StringComparison.OrdinalIgnoreCase)) ||
            (update.Categories ?? new List<string>()).Any(n => string.Equals(n?.Trim(), englishName, StringComparison.OrdinalIgnoreCase));

        public static bool IsValidScope(string scope) => scope == "security" || scope == "all";

        // install_updates kapsamı: security -> güvenlik güncellemeleri, all -> bekleyenlerin hepsi. Dönen: indeksler
        public static List<int> SelectForInstall(IReadOnlyList<PendingUpdate> pending, string scope)
        {
            var selected = new List<int>();
            if (!IsValidScope(scope)) return selected;
            for (int i = 0; i < pending.Count; i++)
                if (scope == "all" || pending[i].IsSecurity) selected.Add(i);
            return selected;
        }

        // Sayımlar bütün bekleyenler üzerinden; listeye en çok 500 güncelleme girer, önce kritik ve güvenlik olanlar
        public static PatchStatusPayload BuildStatus(IReadOnlyList<PendingUpdate> pending, bool rebootRequired, DateTime searchedUtc, DateTime? lastInstallUtc, string lastResult)
        {
            return new PatchStatusPayload
            {
                PendingCount = pending.Count,
                PendingSecurity = pending.Count(u => u.IsSecurity),
                PendingCritical = pending.Count(u => u.IsCritical),
                RebootRequired = rebootRequired,
                LastSearch = Iso(searchedUtc),
                LastInstall = lastInstallUtc.HasValue ? Iso(lastInstallUtc.Value) : null,
                Updates = pending
                    .OrderByDescending(u => u.IsCritical)
                    .ThenByDescending(u => u.IsSecurity)
                    .ThenBy(u => u.Title, StringComparer.OrdinalIgnoreCase)
                    .Take(MaxUpdates)
                    .Select(u => new PatchUpdateItem
                    {
                        Kb = u.Kb,
                        Title = u.Title ?? "",
                        Severity = string.IsNullOrWhiteSpace(u.Severity) ? null : u.Severity.Trim(),
                        Categories = (u.Categories ?? new List<string>()).Where(c => !string.IsNullOrWhiteSpace(c)).ToList(),
                        IsSecurity = u.IsSecurity,
                    })
                    .ToList(),
                LastResult = lastResult,
            };
        }

        // ISO 8601, UTC ("2026-09-27T11:31:27Z"). WUA geçmişindeki zamanlar UTC'dir.
        public static string Iso(DateTime utc)
        {
            if (utc.Kind == DateTimeKind.Local) utc = utc.ToUniversalTime();
            return utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        }

        // Kurulumun kısa Türkçe özeti (panelde last_result), ör. "3 güncelleme kuruldu, 1 başarısız (KB5043080)"
        public static string Summarize(int installed, IReadOnlyList<string> failed, int skipped, bool rebootRequired)
        {
            failed ??= new List<string>();
            if (installed == 0 && failed.Count == 0 && skipped == 0) return "Kurulacak güncelleme yok";
            var parts = new List<string> { $"{installed} güncelleme kuruldu" };
            if (failed.Count > 0) parts.Add($"{failed.Count} başarısız ({string.Join(", ", failed)})");
            if (skipped > 0) parts.Add($"{skipped} atlandı (kullanıcı girişi istiyor)");
            string summary = string.Join(", ", parts);
            if (rebootRequired) summary += "; yeniden başlatma gerekiyor";
            return summary.Length <= 500 ? summary : summary.Substring(0, 497) + "...";
        }

        // Başarısız güncellemenin özetteki adı: KB numarası, yoksa kısaltılmış başlık
        public static string Label(PendingUpdate update)
        {
            if (!string.IsNullOrEmpty(update.Kb)) return update.Kb;
            string title = update.Title ?? "?";
            return title.Length <= 60 ? title : title.Substring(0, 57) + "...";
        }
    }

    // Günlük Windows Update taraması. Laboratuvar makineleri çoğu zaman aynı anda açılır ve geceleri kapalıdır:
    // sabit bir saat seçilseydi kapalı makineler hiç taranmazdı. Bunun yerine ilk tarama açılıştan sonra ajana özgü
    // 10–70 dakikalık bir gecikmeyle yapılır (hw_id'den, her açılışta aynı), sonrakiler son bildirilen taramadan
    // 24 saat sonra. Böylece makineler Windows Update'e aynı anda çıkmaz.
    public static class PatchSchedule
    {
        public static readonly TimeSpan Interval = TimeSpan.FromDays(1);
        public static readonly TimeSpan MinStartupDelay = TimeSpan.FromMinutes(10);
        public static readonly TimeSpan StartupSpread = TimeSpan.FromMinutes(60);
        // Başarısız bir denemeden sonra en erken bu kadar süre sonra yeniden denenir
        public static readonly TimeSpan RetryDelay = TimeSpan.FromHours(1);

        public static TimeSpan StartupDelay(string hwId)
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(hwId ?? ""));
            uint value = BitConverter.ToUInt32(hash, 0);
            return MinStartupDelay + TimeSpan.FromSeconds(value % (uint)StartupSpread.TotalSeconds);
        }

        public static DateTime NextScanUtc(DateTime? lastReportedScanUtc, DateTime? lastAttemptUtc, DateTime startedUtc, DateTime nowUtc, string hwId)
        {
            DateTime due = startedUtc + StartupDelay(hwId);
            // Gelecekte görünen kayıt (saat geri alınmış) yok sayılır; yoksa tarama günlerce ertelenirdi
            if (lastReportedScanUtc.HasValue && lastReportedScanUtc.Value <= nowUtc + TimeSpan.FromMinutes(5))
                due = Later(due, lastReportedScanUtc.Value + Interval);
            if (lastAttemptUtc.HasValue)
                due = Later(due, lastAttemptUtc.Value + RetryDelay);
            return due;
        }

        private static DateTime Later(DateTime a, DateTime b) => a > b ? a : b;
    }
}
