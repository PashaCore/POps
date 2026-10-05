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
    // Alanlar sunucunun şemalarına göre okunur (docs/protocol/server-to-agent/power.json, user_message.json).
    [SupportedOSPlatform("windows")]
    public static class SessionTasks
    {
        // -6: oturum açık kullanıcı yok (logoff/lock ya da mesajı gösterecek kimse yok); sunucu görevi Denied yapar.
        // Aynı sayı CommandRunner.ExitDuplicate'tir; o sunucuya hiç gönderilmez (yinelenen execute yok sayılır), bu
        // gönderilir. İki anlam aynı görevde karşılaşmaz.
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

        // Zorunlu metin alanı: yok, null ya da metin değilse hata (Valid = false)
        public static (bool Valid, string? Value) RequiredString(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.String) return (false, null);
            string? text = ReadString(value);
            return (text != null, text);
        }

        // İsteğe bağlı metin alanı: yok ya da null -> null; metin değilse hata (Valid = false)
        public static (bool Valid, string? Value) OptionalString(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null) return (true, null);
            if (value.ValueKind != JsonValueKind.String) return (false, null);
            string? text = ReadString(value);
            return (text != null, text);
        }

        // requested_by: yalnızca denetim kaydı ve log için; metin değilse yok sayılır
        public static string? RequestedBy(JsonElement root) =>
            root.TryGetProperty("requested_by", out JsonElement value) && value.ValueKind == JsonValueKind.String ? ReadString(value) : null;

        // Eşi olmayan vekil karakter kaçışı ("\ud800") metin olarak okunamaz: alan geçersiz sayılır
        private static string? ReadString(JsonElement value)
        {
            try { return value.GetString(); }
            catch (InvalidOperationException) { return null; }
        }

        // Uzunluk Unicode karakteriyle sayılır (JSON Schema maxLength ve sunucudaki Python len() gibi; emoji 1 karakter)
        public static int Length(string text)
        {
            int count = 0;
            foreach (Rune _ in text.EnumerateRunes()) count++;
            return count;
        }

        // Sunucunun temizliğinin (Backend/pops/power.py clean_text) ajandaki karşılığı; sunucu zaten temiz gönderir,
        // burada yalnızca savunma için yeniden yapılır:
        //  * \r\n ve \r -> \n; U+2028/U+2029 (satır/paragraf ayırıcı) da satır sonu sayılır;
        //  * tek satırlık alanda (başlık, güç notu) her satır sonu, metinde yalnızca sekme bir boşluk olur;
        //  * \n dışındaki kontrol karakterleri (C0, DEL, C1) atılır;
        //  * yazı yönünü değiştiren (U+200E/U+200F, U+202A-U+202E, U+2066-U+2069), sıfır genişlikli (U+200B-U+200D,
        //    U+2060) ve görünmez biçim karakterleri (U+2061-U+2065) ile BOM (U+FEFF) atılır: metin ekranda olduğundan
        //    farklı okunmasın;
        //  * metinde satır sonundaki boşluklar silinir, art arda en çok bir boş satır kalır; baştaki ve sondaki boşluk
        //    kırpılır.
        public static string Clean(string text, bool keepLineBreaks)
        {
            var clean = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') continue;
                if (c == '\r' || c == '\n' || c == '\u2028' || c == '\u2029') clean.Append(keepLineBreaks ? '\n' : ' ');
                else if (c == '\t') clean.Append(' ');
                else if (char.IsControl(c) || IsInvisibleFormat(c)) continue;
                else clean.Append(c);
            }
            if (!keepLineBreaks) return clean.ToString().Trim();

            var lines = new StringBuilder(clean.Length);
            bool previousEmpty = false;
            foreach (string raw in clean.ToString().Split('\n'))
            {
                string line = raw.TrimEnd(' ');
                bool empty = line.Length == 0;
                if (empty && previousEmpty) continue;
                if (lines.Length > 0 || previousEmpty) lines.Append('\n');
                lines.Append(line);
                previousEmpty = empty;
            }
            return lines.ToString().Trim();
        }

        // Sunucunun sildiği aralıklar: U+200B-U+200F, U+202A-U+202E, U+2060-U+2069, U+FEFF
        private static bool IsInvisibleFormat(char c) =>
            (c >= '\u200B' && c <= '\u200F') || (c >= '\u202A' && c <= '\u202E') || (c >= '\u2060' && c <= '\u2069') || c == '\uFEFF';
    }
}
