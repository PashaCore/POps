using System;
using System.Text;
using System.Text.RegularExpressions;

namespace POps.Shared
{
    // Çevrimdışı bypass kodunun biçimi (panel 6 hex karakter üretir; daha uzun önek de kabul edilir). Servis kodu
    // doğrular; tepsi yalnızca biçimi denetler, böylece "O" yerine "0" gibi bir yazım hatası deneme hakkı yemez.
    public static class BypassCode
    {
        private static readonly Regex Pattern = new Regex("^[0-9A-F]{6,64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // Boşlukları atar, büyük harfe çevirir; biçim uymuyorsa null
        public static string? Normalize(string? code)
        {
            string value = (code ?? "").Trim().ToUpperInvariant();
            return Pattern.IsMatch(value) ? value : null;
        }

        public static bool IsWellFormed(string? code) => Normalize(code) != null;
    }

    // Dışarıdan gelen metni (sunucu komutu, tepsi mesajı) loga yazmadan önce: denetim karakterleri ve satır
    // sonları "?" olur (sahte log satırı eklenemez), uzun metin kısaltılır.
    public static class LogText
    {
        public static string Safe(string? value, int maxLength = 60)
        {
            if (value == null) return "(yok)";
            var sb = new StringBuilder(Math.Min(value.Length, maxLength + 1));
            foreach (char c in value)
            {
                if (sb.Length == maxLength) { sb.Append('…'); break; }
                bool control = char.IsControl(c) || c == '\u2028' || c == '\u2029';
                sb.Append(control ? '?' : c);
            }
            return sb.ToString();
        }
    }
}
