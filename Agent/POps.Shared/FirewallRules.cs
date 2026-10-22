using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

// Bu dosya hem ajanda (POps.Shared, net10.0) hem MSI custom action'larında (net472, bağlantılı derleme) kullanılır:
// yalnızca iki çerçevede de bulunan API'ler.
namespace POps.Shared
{
    // POps'un Windows Güvenlik Duvarı'na eklediği kurallar ve onları kayda geçiren durum dosyaları, tek yerde. Ajan kurallarını
    // yalnızca bu gruplarda kurar: ağ karantinası ve sınav modu (NetworkIsolation; sınav modu aynı motoru kendi grubuyla kullanır)
    // ve eş önbelleği (PeerCache, TCP 8817). Karantina ve sınav modu kurulurken bütün güvenlik duvarı profillerini açar ve önceden
    // kapalı olanları durum dosyasına yazar.
    // MSI gerçekten kaldırılırken (güncellemede değil; Installer/agent/CustomActions, RemoveConfig) bu gruplardaki bütün kurallar
    // silinir, önceden kapalı olan profiller yeniden kapatılır ve durum dosyaları silinir (BuildUninstallScript): karantinada ya
    // da sınavdayken kaldırılan ajan bilgisayarı kilitli bırakmaz. Yeni bir kural grubu Groups'a eklenmeden kural kuramaz: ajanın
    // betik kurucusu listede olmayan grubu reddeder, testler de ürün kodunda kural ekleyen her betiği bu listeyle karşılaştırır.
    public static class FirewallRules
    {
        public const string IsolationGroup = "POps Isolation";
        public const string ExamGroup = "POps Exam";
        public const string PeerCacheGroup = "POps Peer Cache";

        // Ajanın kurduğu bütün kural grupları; kaldırma hepsini siler
        public static IReadOnlyList<string> Groups { get; } = new[] { IsolationGroup, ExamGroup, PeerCacheGroup };

        // 0.1.4-alpha'dan eski ajanların grupsuz karantina kuralları (POps_Isolation_BlockOut, ...; görünen ad deseni)
        public const string LegacyIsolationRules = "POps_Isolation_*";

        // Windows Güvenlik Duvarı profilleri (Get-NetFirewallProfile adları); betiklere yalnızca bunlar girer
        public static IReadOnlyList<string> Profiles { get; } = new[] { "Domain", "Private", "Public" };

        // Ajanın güvenli depodaki (<veri klasörü>\secure) durum dosyaları: varken karantina / sınav modu / kilit ekranı sürer
        // ve ajan kuralları yeniden kurar. Kaldırma, kurallar silindikten sonra bunları da siler.
        public const string IsolationStateFile = "isolation.json";
        public const string ExamStateFile = "exam.json";
        public const string QuarantineLockFile = "lockdown.json";
        public static IReadOnlyList<string> StateFiles { get; } = new[] { IsolationStateFile, ExamStateFile, QuarantineLockFile };

        // Kurallardan önceki profil durumu: isolation.json'da [{"Name","Enabled"}] (Get-NetFirewallProfile'ın Name ve Enabled
        // alanları, Enabled "True"/"False"), exam.json'da önceden kapalı profillerin adları
        public const string IsolationProfilesKey = "previous_profiles";
        public const string ExamProfilesKey = "previous_disabled_profiles";

        public static bool IsGroup(string? group) => group != null && Groups.Contains(group, StringComparer.Ordinal);

        // Bilinen profil adları, yazımları düzeltilmiş ve bir kez; başka hiçbir değer betiğe girmez
        public static List<string> KnownProfiles(IEnumerable<string?>? names)
        {
            var result = new List<string>();
            foreach (string? name in names ?? Enumerable.Empty<string?>())
            {
                string? profile = Profiles.FirstOrDefault(p => string.Equals(p, name?.Trim(), StringComparison.OrdinalIgnoreCase));
                if (profile != null && !result.Contains(profile, StringComparer.Ordinal)) result.Add(profile);
            }
            return result;
        }

        // Kaldırma betiği (Windows PowerShell 5.1, NetSecurity): Groups'taki gruplardaki ve eski adlı bütün kurallar silinir,
        // profilesToDisable (karantinadan / sınavdan önce kapalı olanlar) yeniden kapatılır, sonra kalan kural aranır.
        // Güvenlik duvarı sorgulanamazsa, bir kural silinemezse ya da kural kalırsa çıkış 2 ve nedenler; başarıda "OK <sayı>".
        // Kurallar tek sorguyla okunur: sorgu hata verirse (ör. güvenlik duvarı servisi yanıt vermiyor) boş sonuç "temiz"
        // sayılmaz. Betiğe yalnızca sabit metin, grup adları ve bilinen profil adları girer.
        public static string BuildUninstallScript(IEnumerable<string?>? profilesToDisable)
        {
            var sb = new StringBuilder();
            sb.AppendLine("$ErrorActionPreference = 'Stop'");
            // NetSecurity modülünün ilk yüklenişindeki ilerleme kayıtları (CLIXML) çıktıya, oradan MSI log'una karışmasın
            sb.AppendLine("$ProgressPreference = 'SilentlyContinue'");
            sb.AppendLine("$groups = @(" + string.Join(", ", Groups.Select(Quote)) + ")");
            sb.AppendLine("$legacy = " + Quote(LegacyIsolationRules));
            sb.AppendLine("function Get-POpsRules { Get-NetFirewallRule | Where-Object { ($groups -contains $_.Group) -or ($_.DisplayName -like $legacy) } }");
            sb.AppendLine("$problems = @()");
            sb.AppendLine("$removed = 0");
            sb.AppendLine("$rules = @()");
            sb.AppendLine("try { $rules = @(Get-POpsRules) } catch { $problems += 'rules: ' + $_.Exception.Message }");
            // Her kural ayrı silinir: biri silinemezse ötekiler yine kalkar
            sb.AppendLine("foreach ($rule in $rules) {");
            sb.AppendLine("    try { $rule | Remove-NetFirewallRule; $removed++ } catch { $problems += $rule.DisplayName + ': ' + $_.Exception.Message }");
            sb.AppendLine("}");
            // Kurallar kalksa da kalkmasa da profiller kurallardan önceki hâline döner
            foreach (string profile in KnownProfiles(profilesToDisable))
                sb.AppendLine("try { Set-NetFirewallProfile -Profile " + profile + " -Enabled False } catch { $problems += '" + profile + ": ' + $_.Exception.Message }");
            sb.AppendLine("$left = @()");
            sb.AppendLine("try { $left = @(Get-POpsRules) } catch { $problems += 'check: ' + $_.Exception.Message }");
            sb.AppendLine("if ($left.Count -gt 0) { $problems += 'left: ' + (($left | ForEach-Object { $_.DisplayName }) -join ', ') }");
            sb.AppendLine("if ($problems.Count -gt 0) { $problems -join '; '; exit 2 }");
            sb.AppendLine("'OK ' + $removed");
            return sb.ToString();
        }

        // PowerShell tek tırnaklı dize: içindeki tek tırnak ikilenir
        private static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
    }
}
