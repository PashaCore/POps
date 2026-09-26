using System;
using System.IO;
using System.Text.Json;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
namespace POpsAgent
{
    public static class POpsHelpers
    {
        // Klasör yolları POps standartlarına göre güncellendi
        private static readonly string LogDir = @"C:\POpsLogs";
        private static readonly object LogLock = new object();

        // Ayar dosyası önce ajanın kurulu olduğu klasörde (ör. C:\Program Files (x86)\POps), sonra eski
        // sabit konumda (C:\POps) aranır; her ayar için ilk dolu değer kullanılır. Yalnızca C:\POps'a
        // bakıldığında başka klasöre kurulan ajanlar sunucu adresini bulamıyordu.
        public static readonly string[] ConfigPaths =
        {
            Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
            @"C:\POps\appsettings.json",
        };

        // ==========================================
        // 0. SÜRÜM (TEK KAYNAK)
        // ==========================================
        // Sürüm kök VERSION dosyasından gelir (Directory.Build.props -> assembly). Koda gömülmez.
        // "+<commit>" derleme meta verisi varsa atılır; önüne "v" eklenir (ör. "v0.1.2-alpha").
        public static string AppVersion
        {
            get
            {
                var asm = Assembly.GetExecutingAssembly();
                string v = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                           ?? asm.GetName().Version?.ToString()
                           ?? "0.0.0";
                int plus = v.IndexOf('+');
                if (plus >= 0) v = v.Substring(0, plus);
                return v.StartsWith("v") ? v : "v" + v;
            }
        }

        // ==========================================
        // 1. MERKEZİ VE NİZAMLI LOGLAMA
        // ==========================================
        public static void Log(string component, string message, bool isError = false)
        {
            try
            {
                if (!Directory.Exists(LogDir))
                    Directory.CreateDirectory(LogDir);

                string dateStr = DateTime.Now.ToString("yyyyMMdd");
                string logFile = Path.Combine(LogDir, $"POps_{dateStr}.log"); // Log dosya adı POps oldu

                string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
                string errorTag = isError ? "[ERROR]" : "[INFO ]";
                string logLine = $"[{timestamp}] {errorTag} [{component}] {message}{Environment.NewLine}";

                // Multi-process çakışmalarını önlemek için kilit (Lock)
                lock (LogLock)
                {
                    File.AppendAllText(logFile, logLine);
                }

                // Konsol ekranı açıksa oraya da renkli yaz (Debug için)
                if (Environment.UserInteractive)
                {
                    Console.ForegroundColor = isError ? ConsoleColor.Red : ConsoleColor.Yellow;
                    Console.Write($"[{timestamp}] [{component}] ");
                    Console.ResetColor();
                    Console.WriteLine(message);
                }
            }
            catch
            {
                // Log yazarken hata olursa sistemi çökertme, yut.
            }
        }

        // ==========================================
        // 2. ORTAK CONFIG (IP) OKUMA
        // ==========================================
        public static string GetServerUrl()
        {
            string defaultUrl = "http://127.0.0.1:8000"; // Son çare (Fallback)

            // Sunucu adresi koda gömülmez: önce POPS_SERVER_URL ortam değişkeni, sonra appsettings.json
            string envUrl = Environment.GetEnvironmentVariable("POPS_SERVER_URL");
            if (!string.IsNullOrWhiteSpace(envUrl))
            {
                return envUrl.Trim().TrimEnd('/');
            }

            string url = ReadConfigValue("ServerUrl");
            if (url != null)
            {
                return url.TrimEnd('/');
            }

            Log("HELPERS", $"ServerUrl tanımlı değil ({string.Join(" | ", ConfigPaths)}); {defaultUrl} kullanılıyor.", true);
            return defaultUrl;
        }

        // Cihaz secret'ı, enroll jetonu ve sunucunun gönderdiği komutlar (execute, set_secret, set_identity)
        // yalnızca şifreli kanaldan (https/wss) taşınır: düz ws:// üzerinde aynı ağdaki biri bunları okuyup
        // SYSTEM olarak komut gönderebilirdi. Düz http yalnızca aynı makinedeki (loopback) sunucu için kabul edilir.
        public static bool IsSecureServerUrl(string url) =>
            Uri.TryCreate(url, UriKind.Absolute, out Uri uri)
            && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback));

        // Gizli olmayan bir ayar: önce sistem ortam değişkeni, sonra appsettings.json.
        public static string GetSetting(string key, string envVar)
        {
            string env = Environment.GetEnvironmentVariable(envVar);
            return !string.IsNullOrWhiteSpace(env) ? env.Trim() : ReadConfigValue(key);
        }

        // Bir ayarı sırayla ConfigPaths içindeki dosyalarda arar; hiçbirinde dolu değilse null döner.
        public static string ReadConfigValue(string key)
        {
            foreach (string path in ConfigPaths)
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
                    if (doc.RootElement.TryGetProperty(key, out JsonElement element) && element.ValueKind == JsonValueKind.String)
                    {
                        string value = element.GetString();
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            return value.Trim();
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log("HELPERS", $"Config okuma hatası ({path}): {ex.Message}", true);
                }
            }
            return null;
        }

        // ==========================================
        // 3. ORTAK KİMLİK (HW_ID) OKUMA
        // ==========================================
        public static string GetHardwareId()
        {
            string identityPath = @"C:\POpsData\identity.key"; // Kimlik yolu güncellendi
            try
            {
                if (File.Exists(identityPath))
                {
                    string savedId = File.ReadAllText(identityPath).Trim();
                    if (!string.IsNullOrEmpty(savedId) && savedId.StartsWith("HW-"))
                    {
                        return savedId;
                    }
                }
            }
            catch { }

            return "HW-UNKNOWN";
        }

        // ==========================================
        // 4. WAKE-ON-LAN YAYINI (P2P UYANDIRMA)
        // ==========================================
        public static void SendWolPacket(string macAddress)
        {
            try
            {
                string cleanMac = macAddress.Replace(":", "").Replace("-", "").Replace(".", "").Trim();
                if (cleanMac.Length != 12)
                {
                    Log("HELPERS", $"WOL Hatası: Geçersiz MAC adresi formatı ({macAddress})", true);
                    return;
                }

                byte[] macBytes = new byte[6];
                for (int i = 0; i < 6; i++)
                {
                    macBytes[i] = Convert.ToByte(cleanMac.Substring(i * 2, 2), 16);
                }

                byte[] packet = new byte[102];
                for (int i = 0; i < 6; i++) packet[i] = 0xFF;
                for (int i = 1; i <= 16; i++)
                {
                    for (int j = 0; j < 6; j++)
                    {
                        packet[i * 6 + j] = macBytes[j];
                    }
                }

                using UdpClient client = new UdpClient();
                client.EnableBroadcast = true;
                client.Send(packet, packet.Length, new IPEndPoint(IPAddress.Broadcast, 9));
                Log("HELPERS", $"WOL Sihirli Paketi fırlatıldı: {macAddress}");
            }
            catch (Exception ex)
            {
                Log("HELPERS", $"WOL Gönderim hatası ({macAddress}): {ex.Message}", true);
            }
        }
    }
}