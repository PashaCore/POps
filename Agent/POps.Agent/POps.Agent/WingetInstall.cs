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
    //   install --id <id> -e --source winget --silent --scope machine --accept-package-agreements --accept-source-agreements
    //   --disable-interactivity [--version <v>]
    // --source winget: yalnızca topluluk kaynağı; SYSTEM olarak msstore kaynağı devreye girmez. Sürüm verilmediyse önce kaynaktaki
    // en yeni sürüm sorulur ve kurulum ona sabitlenir (bkz. VersionsArguments, LatestVersion): sürümsüz ve --scope machine ile
    // winget, machine yükleyicisi olan en yeni sürümü sessizce seçerdi (ör. Anki.Anki: 26.09.3 yerine 2023'ün 2.1.65'i).
    // Yalnızca kullanıcı kapsamında yükleyicisi olan paket (sürüm) --scope machine ile "uygulanabilir yükleyici yok"
    // (ExitNoApplicableInstaller) ile biter: aynı görevde bir kez --scope olmadan yeniden denenir (bkz. RetryArguments). Çıktının
    // ilk satırı kullanılan kapsamdır; sürümü ajan seçtiyse ikinci satır o sürümdür.
    // Sonuç execute gibi "result"tır; çıkış kodu winget'in kendi kodudur (işaretli 32 bit, ör. 0x8A150061 = -1978335135;
    // yeniden denendiyse ikinci denemenin kodu).
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

        // winget'in APPINSTALLER_CLI_ERROR_NO_APPLICABLE_INSTALLER kodu, 0x8A150010 = -1978335216: "None of the installers are
        // applicable for the current system"
        // (https://github.com/microsoft/winget-cli/blob/master/doc/windows/package-manager/winget/returnCodes.md). --scope
        // machine ile yalnızca kullanıcı kapsamında yükleyicisi olan paket bu kodla biter.
        public const int ExitNoApplicableInstaller = unchecked((int)0x8A150010);
        // Çıktının ilk satırı: paket hangi kapsamla kuruldu (denendi)
        public const string MachineScopeNote = "[kapsam: machine]";
        public const string UserScopeNote = "[kapsam: user — machine yükleyici yok, kapsamsız yeniden denendi]";
        // Yeniden denendiyse ilk denemenin çıktısından en çok bu kadarı (asıl çıktı ikinci denemenin; ilki birkaç satırdır)
        internal const int MachineAttemptMaxChars = 4096;
        // Sürüm sorgusunun kendi süre sınırı; görevin süre sınırı, cancel_task ve servis durması onu da kapsar
        public static readonly TimeSpan QueryLimit = TimeSpan.FromSeconds(60);
        // Görev sürüm sorgusu sırasında durdu (cancel_task, servis durması, süre sınırı): kurulum çalışmadı, kapsam satırı yok
        public const string LookupStoppedNote = "[sürüm: en yeni sürüm sorgulanırken görev durdu; kurulum başlamadı]";

        // Tam eşleşme: \z sondaki satır sonunu da kabul etmez ($ ederdi)
        private static readonly Regex IdRegex = new Regex(@"^[A-Za-z0-9][A-Za-z0-9.+_-]{1,127}\z", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex VersionRegex = new Regex(@"^[0-9A-Za-z.+_-]{1,40}\z", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex MeasureRegex = new Regex(@"^[0-9.,%KMGBkib]*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly char[] BarChars = { '█', '▒', '▓', '░' };
        private static readonly string[] MachineScope = { "--scope", "machine" };
        private static readonly string[] Unattended = { "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity" };

        // Servisin winget.exe araması (null: yok; yalnızca MachineSeams.Production kurar, bkz. MachineSeams.WingetLocator)
        internal static string? LocateWinget() => Find(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps"));

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

        // machineScope false: kapsamsız yeniden deneme (--scope machine yok; bkz. RetryArguments)
        public static List<string> Arguments(string id, string? version, bool machineScope = true)
        {
            var args = new List<string> { "install", "--id", id, "-e", "--source", "winget", "--silent" };
            if (machineScope) args.AddRange(MachineScope);
            args.AddRange(Unattended);
            if (version != null) args.AddRange(new[] { "--version", version });
            return args;
        }

        // Kaynaktaki sürümlerin listesi (en yenisi başta; bkz. LatestVersion). Hiçbir şey kurmaz.
        public static List<string> VersionsArguments(string id) =>
            new List<string> { "show", "--id", id, "-e", "--source", "winget", "--versions", "--accept-source-agreements", "--disable-interactivity" };

        // Bir winget sorgusunun (show, list) neden sonuç vermediği; null: bitti ve 0 ile çıktı. limitReached: kendi süre sınırında
        // (limit) bitmedi
        public static string? QueryFailure(string command, int exitCode, bool limitReached, TimeSpan limit) =>
            limitReached ? $"winget {command} {limit.TotalSeconds:0} sn içinde bitmedi" : exitCode != 0 ? $"winget {command} çıkış kodu {exitCode}" : null;

        // "winget show --versions" çıktısından kaynaktaki en yeni sürüm. winget sürümleri tek sütunlu bir tabloda en yenisi başta
        // yazar; sütun başlığı yerelleştirilir ("Version", "Sürüm"), altındaki tire satırı yerelleştirilmez: ondan sonraki ilk satır
        // alınır. Değer sunucudan gelen sürümün kuralına uymalıdır (VersionRegex): boş, boşluklu ya da tablonun kısalttığı ("…")
        // değer kabul edilmez. null: bulunamadı, reason kısa nedeni söyler.
        public static string? LatestVersion(string output, out string reason)
        {
            reason = "";
            bool table = false;
            foreach (string raw in FinalLines(output ?? ""))
            {
                string line = raw.Trim();
                if (!table)
                {
                    table = line.Length >= 3 && line.All(c => c == '-');
                    continue;
                }
                if (line.Length == 0 || IsProgress(line)) continue;
                if (VersionRegex.IsMatch(line)) return line;
                reason = "listedeki ilk sürüm geçersiz ya da kısaltılmış";
                return null;
            }
            reason = table ? "sürüm listesi boş" : "çıktıda sürüm listesi yok";
            return null;
        }

        // Sürümü sunucu vermediyse çıktının ikinci satırı: ajanın seçtiği sürüm ya da neden seçemediği
        public static string PinnedNote(string version) => $"[sürüm: kaynaktaki en yeni sürüm {version} seçildi]";
        public static string UnpinnedNote(string reason) => $"[sürüm: en yeni sürüm bulunamadı ({reason}); winget kendisi seçti]";

        // İlk denemenin (--scope machine) çıkış kodundan, aynı görevdeki tek yeniden denemenin bağımsız değişkenleri: yalnızca
        // "uygulanabilir yükleyici yok" kodunda, --scope olmadan. null: yeniden denenmez.
        public static List<string>? RetryArguments(int machineExitCode, string id, string? version) =>
            machineExitCode == ExitNoApplicableInstaller ? Arguments(id, version, machineScope: false) : null;

        // Sunucuya giden çıktı: ilk satır kullanılan kapsam, sürümü ajan seçtiyse ardından versionNote (PinnedNote ya da
        // UnpinnedNote), sonra winget'in temizlenmiş çıktısı. machineAttempt: kapsamsız yeniden denendiyse ilk denemenin (--scope
        // machine) çıktısı; iki denemenin çıktısı sırayla yazılır.
        public static string ResultOutput(string output, string? machineAttempt = null, string? versionNote = null)
        {
            string notes = versionNote == null ? "" : "\n" + versionNote;
            if (machineAttempt == null) return CommandExecutionPolicy.TruncateOutput(MachineScopeNote + notes + "\n" + CleanOutput(output));
            string first = CleanOutput(machineAttempt);
            if (first.Length > MachineAttemptMaxChars) first = string.Concat(first.AsSpan(0, MachineAttemptMaxChars), "\n[ÇIKTI KISALTILDI]");
            return CommandExecutionPolicy.TruncateOutput($"{UserScopeNote}{notes}\n[1. deneme, --scope machine: çıkış {ExitNoApplicableInstaller}]\n"
                + $"{first}\n[2. deneme, kapsamsız]\n{CleanOutput(output)}");
        }

        // Görev sürüm sorgusu sırasında durduysa çıktı: kurulum çalışmadı (kapsam satırı yok), ardından durma nedeni
        public static string LookupStoppedOutput(string output) => CommandExecutionPolicy.TruncateOutput(LookupStoppedNote + "\n" + CleanOutput(output));

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
            foreach (string line in FinalLines(output))
            {
                if (line.Length > 0 && IsProgress(line)) continue;
                lines.Add(line);
            }
            while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
            return string.Join("\n", lines);
        }

        // Çıktının satırları, her birinin son hâliyle: winget satır başına (\r) dönüp aynı satırı yeniden çizer
        private static IEnumerable<string> FinalLines(string output)
        {
            foreach (string raw in output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
            {
                string line = raw;
                int cr = line.TrimEnd('\r').LastIndexOf('\r');
                if (cr >= 0) line = line.Substring(cr + 1);
                yield return line.TrimEnd('\r', ' ');
            }
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
