#nullable enable

using System;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace POpsAgent
{
    // Uzaktan güç işlemleri (power) ve kullanıcı mesajlarının (user_message) ortak kuralları: oturum denetimi, metin
    // temizliği, alan okuma ve "kullanıcı yok" sonucu. Makineye dokunan her şey (oturum sorgusu, bekleme) burada
    // değiştirilebilir bir giriştir; testler sahtelerini koyar (bkz. POps.Tests TestEnvironment.EnsureIsolated).
    [SupportedOSPlatform("windows")]
    public static class SessionTasks
    {
        // -6: oturum açık kullanıcı yok (logoff/lock ya da gösterilecek kimse yok). Aynı sayı CommandRunner.ExitDuplicate'tir;
        // o sunucuya hiç gönderilmez (yinelenen execute yok sayılır), bu gönderilir. İki anlam aynı görevde karşılaşmaz.
        public const int ExitNoUser = -6;
        public const string NoUserOutput = "[REDDEDİLDİ] oturum açık kullanıcı yok";

        // Etkin konsol oturumunda (fiziksel ekran) oturum açmış kullanıcı var mı. Yalnızca SYSTEM'de doğru yanıt verir
        // (WTSQueryUserToken); başka hesapta hep false.
        internal static Func<bool> HasConsoleUser { get; set; } = ConsoleUserPresent;

        // Etkin konsol oturumunun kimliği (kilitleme: tepsi bu oturumda mı)
        internal static Func<uint> ConsoleSession { get; set; } = UserSessionLauncher.ActiveConsoleSession;

        // Geri sayım ve okundu onayı beklemesi
        internal static Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;

        private static bool ConsoleUserPresent() => UserSessionLauncher.HasSignedInUser(UserSessionLauncher.ActiveConsoleSession());

        // task_id: 1 ya da daha büyük bir tamsayı. Yoksa sonuç bildirilemez; çağıran mesajı yok sayar.
        public static int? TaskId(JsonElement root) =>
            root.ValueKind == JsonValueKind.Object && root.TryGetProperty("task_id", out JsonElement id) && id.ValueKind == JsonValueKind.Number
            && id.TryGetInt32(out int value) && value >= 1 ? value : null;

        // İsteğe bağlı metin alanı: yok ya da null -> null; metin değilse hata (Valid = false)
        public static (bool Valid, string? Value) OptionalString(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null) return (true, null);
            return value.ValueKind == JsonValueKind.String ? (true, value.GetString()) : (false, null);
        }

        // requested_by: yalnızca denetim kaydı ve log için; metin değilse yok sayılır
        public static string? RequestedBy(JsonElement root) =>
            root.TryGetProperty("requested_by", out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        // Uzunluk Unicode karakteriyle sayılır (JSON Schema maxLength ve sunucudaki Python len() gibi; emoji 1 karakter)
        public static int Length(string text)
        {
            int count = 0;
            foreach (Rune _ in text.EnumerateRunes()) count++;
            return count;
        }

        // Kontrol karakterleri atılır; sekme ve (tek satırlık alanda) satır sonu boşluğa döner. Uzun metinde satır sonları
        // "\n" olarak kalır. Yazı yönünü değiştiren karakterler (U+202A-202E, U+2066-2069) de atılır: başlık ve metin
        // ekranda olduğundan farklı okunmasın.
        public static string Clean(string text, bool keepLineBreaks)
        {
            string source = text.Replace("\r\n", "\n", StringComparison.Ordinal);
            var clean = new StringBuilder(source.Length);
            foreach (char c in source)
            {
                if (c == '\n' && keepLineBreaks) clean.Append('\n');
                else if (c == '\t' || c == '\n' || c == '\r') clean.Append(' ');
                else if (char.IsControl(c) || (c >= '‪' && c <= '‮') || (c >= '⁦' && c <= '⁩')) continue;
                else clean.Append(c);
            }
            return clean.ToString().Trim();
        }
    }
}
