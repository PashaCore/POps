using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

#nullable enable

namespace POpsAgent
{
    // winget_install (Dağıtım'ın winget adımı; sözleşme docs/agent.md "winget_install contract"). Sunucu bu eylemi yalnızca
    // X-Agent-Features'ta "winget" duyuran ajana gönderir:
    //   {"action":"winget_install","task_id":42,"id":"Mozilla.Firefox","version":null,"requested_by":"admin"}
    // Ajan yerel terminal yeteneği ve sınıfın deploy modülü açıksa, kimlik ve sürümü kendisi de doğrulayıp winget.exe'yi
    // SYSTEM olarak, kabuk olmadan, her bağımsız değişkeni ayrı vererek çalıştırır:
    //   install --id <id> -e --silent --scope machine --accept-package-agreements --accept-source-agreements
    //   --disable-interactivity [--version <v>]
    // Sonuç execute gibi "result"tır; çıkış kodu winget'in kendi kodudur (işaretli 32 bit, ör. 0x8A150061 = -1978335135).
    [SupportedOSPlatform("windows")]
    public static class WingetInstall
    {
        public const string Action = "winget_install";
        public const string Feature = "winget";
        // Sunucu -7'yi "Denied" sayar (winget yok); -8'i yalnızca sunucu kullanır (özelliği duyurmayan ajan)
        public const int ExitMissing = -7;
        public const string MissingMessage = "[REDDEDİLDİ] winget bu bilgisayarda yok";
        public const string InvalidMessage = "[REDDEDİLDİ] Geçersiz winget paketi kimliği ya da sürümü; kurulmadı.";
        public const string TerminalOffMessage = "[REDDEDİLDİ] Bu cihazda uzaktan terminal kapalı (yetenek politikası); winget kurulumu yapılmadı.";
        public const string DeployOffMessage = "[REDDEDİLDİ] Dağıtım modülü bu cihazın sınıfında kapalı (sunucu politikası); winget kurulumu yapılmadı.";

        // Tam eşleşme: \z sondaki satır sonunu da kabul etmez ($ ederdi)
        private static readonly Regex IdRegex = new Regex(@"^[A-Za-z0-9][A-Za-z0-9.+_-]{1,127}\z", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex VersionRegex = new Regex(@"^[0-9A-Za-z.+_-]{1,40}\z", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex MeasureRegex = new Regex(@"^[0-9.,%KMGBkib]*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly char[] BarChars = { '█', '▒', '▓', '░' };

        // Testler: winget.exe'nin yeri (null: yok)
        internal static Func<string?> Locator { get; set; } = () => Find(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps"));

        // Dönen false: id ya da sürüm geçersiz (sürüm null/yok = en yenisi)
        public static bool TryParse(JsonElement command, out string id, out string? version)
        {
            id = "";
            version = null;
            if (!command.TryGetProperty("id", out JsonElement i) || i.ValueKind != JsonValueKind.String) return false;
            string? value = i.GetString();
            if (value == null || !IdRegex.IsMatch(value)) return false;
            if (command.TryGetProperty("version", out JsonElement v) && v.ValueKind != JsonValueKind.Null)
            {
                string? text = v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                if (text == null || !VersionRegex.IsMatch(text)) return false;
                version = text;
            }
            id = value;
            return true;
        }

        public static List<string> Arguments(string id, string? version)
        {
            var args = new List<string>
            {
                "install", "--id", id, "-e", "--silent", "--scope", "machine",
                "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity",
            };
            if (version != null) args.AddRange(new[] { "--version", version });
            return args;
        }

        // Yerel olay günlüğü için komut metni (kimlik ve sürüm açıkça yazılabilir)
        public static string Describe(string id, string? version) => version == null ? $"winget install {id}" : $"winget install {id} --version {version}";

        // WindowsApps\Microsoft.DesktopAppInstaller_<sürüm>_<mimari>__8wekyb3d8bbwe\winget.exe; işletim sisteminin
        // mimarisine uyan en yeni sürüm, yalnızca o klasörden (SYSTEM için PATH'te winget yoktur)
        public static string? Find(string windowsApps)
        {
            if (!Directory.Exists(windowsApps)) return null;
            string arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
            try
            {
                return Directory.EnumerateDirectories(windowsApps, "Microsoft.DesktopAppInstaller_*__8wekyb3d8bbwe")
                    .Select(dir => (Dir: dir, Parts: Path.GetFileName(dir).Split('_')))
                    .Where(d => d.Parts.Length >= 3 && string.Equals(d.Parts[2], arch, StringComparison.OrdinalIgnoreCase)
                                && Version.TryParse(d.Parts[1], out _) && File.Exists(Path.Combine(d.Dir, "winget.exe")))
                    .OrderByDescending(d => Version.Parse(d.Parts[1]))
                    .Select(d => Path.Combine(d.Dir, "winget.exe"))
                    .FirstOrDefault();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return null; }
        }

        // winget'in satır başına (\r) çizdiği ilerleme çubuğu ve dönen imleç atılır: her satırın son hâli kalır, yalnızca
        // çubuk/imleç karakterlerinden oluşan satırlar düşer
        public static string CleanOutput(string output)
        {
            if (string.IsNullOrEmpty(output)) return output;
            var lines = new List<string>();
            foreach (string raw in output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
            {
                string line = raw;
                int cr = line.TrimEnd('\r').LastIndexOf('\r');
                if (cr >= 0) line = line.Substring(cr + 1);
                line = line.TrimEnd('\r', ' ');
                if (line.Length > 0 && IsProgress(line)) continue;
                lines.Add(line);
            }
            while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
            return string.Join("\n", lines);
        }

        private static bool IsProgress(string line)
        {
            var rest = new StringBuilder();
            foreach (char c in line)
            {
                if (c is '-' or '\\' or '|' or '/' or ' ' or '█' or '▒' or '▓' or '░') continue;
                rest.Append(c);
            }
            // "██████▒▒▒▒  45.2 MB / 100 MB" ya da "  12%": çubuk karakteri varsa geri kalan yalnızca ölçü olmalı
            string left = rest.ToString();
            if (left.Length == 0) return true;
            return line.IndexOfAny(BarChars) >= 0 && MeasureRegex.IsMatch(left);
        }
    }
}
