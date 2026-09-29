using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Text.Json;

#nullable disable

namespace POpsAgent
{
    // Karantina sürerken Ctrl+Alt+Del seçeneklerinin kapatılması (bkz. POps.Shared.KioskPolicies). Kayıt:
    // C:\POpsData\secure\kiosk-policies.json (yalnızca SYSTEM/Administrators). Uygulanır: kilitte, açılışta kilit
    // sürüyorsa ve tepsi bağlanınca (sonradan oturum açan kullanıcı). Geri alınır: kilit açılınca (panel, bypass kodu),
    // açılışta kilit yoksa, tepsi bağlanınca kilit yoksa (karantinada oturumu kapatmış kullanıcı döndü) ve MSI
    // kaldırılırken. Hiçbir hata kilit/açma akışını durdurmaz; yalnızca loglanır.
    [SupportedOSPlatform("windows")]
    public static class KioskMode
    {
        public const string FileName = "kiosk-policies.json";
        public static string RecordPath => SecureStore.PathOf(FileName);

        // Testlerde sahte kayıt defteriyle değiştirilir
        public static IKioskRegistry Registry { get; set; } = new WindowsKioskRegistry();

        private static readonly object Gate = new object();

        public static void Engage()
        {
            lock (Gate)
            {
                try
                {
                    List<KioskEntry> record = KioskPolicies.Plan(Registry, Load());
                    // Önce önceki değerler diske: yazdıktan sonra çökse de geri dönülebilir
                    SecureStore.WriteProtected(RecordPath, JsonSerializer.Serialize(record));
                    List<string> errors = KioskPolicies.Enforce(Registry, record);
                    if (errors.Count > 0) POpsHelpers.Log("KIOSK", $"[GÜVENLİK] Kilit politikalarının bir kısmı yazılamadı: {string.Join("; ", errors)}", true);
                    else POpsHelpers.Log("KIOSK", "Karantina: Görev Yöneticisi, oturumu kapat, kullanıcı değiştir, parola değiştir ve kilitle kapatıldı.");
                }
                catch (Exception ex) { POpsHelpers.Log("KIOSK", $"[GÜVENLİK] Kilit politikaları uygulanamadı: {ex.Message}", true); }
            }
        }

        public static void Release()
        {
            lock (Gate)
            {
                try
                {
                    if (!File.Exists(RecordPath)) return;
                    List<KioskEntry> pending = KioskPolicies.Restore(Registry, Load());
                    if (pending.Count == 0)
                    {
                        SecureStore.Delete(RecordPath);
                        POpsHelpers.Log("KIOSK", "Karantina bitti: Ctrl+Alt+Del seçenekleri önceki haline döndü.");
                    }
                    else
                    {
                        SecureStore.WriteProtected(RecordPath, JsonSerializer.Serialize(pending));
                        POpsHelpers.Log("KIOSK", $"{pending.Count} kilit ayarı şimdi geri alınamadı (kullanıcı oturumu kapalı); o kullanıcı oturum açınca geri alınacak.");
                    }
                }
                catch (Exception ex) { POpsHelpers.Log("KIOSK", $"[GÜVENLİK] Kilit politikaları geri alınamadı: {ex.Message}", true); }
            }
        }

        // Servis açılışı ve tepsi bağlantısı: kilit sürüyorsa uygula, sürmüyorsa kalıntıyı temizle
        public static void Sync(bool locked)
        {
            if (locked) Engage();
            else Release();
        }

        private static List<KioskEntry> Load()
        {
            try
            {
                string text = SecureStore.Read(RecordPath);
                return text == null ? new List<KioskEntry>() : JsonSerializer.Deserialize<List<KioskEntry>>(text) ?? new List<KioskEntry>();
            }
            catch (JsonException ex)
            {
                POpsHelpers.Log("KIOSK", $"[GÜVENLİK] {RecordPath} okunamadı ({ex.Message}); önceki değerler bilinmiyor.", true);
                return new List<KioskEntry>();
            }
        }
    }
}
