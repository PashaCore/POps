using System;
using System.IO;
using System.Text.Json;
using System.Net;
using System.Net.Sockets;
namespace POpsAgent
{
    public static class POpsHelpers
    {
        // Klasör yolları POps standartlarına göre güncellendi
        private static readonly string LogDir = @"C:\POpsLogs";
        private static readonly string ConfigPath = @"C:\POps\appsettings.json";
        private static readonly object LogLock = new object();

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
            string defaultUrl = "http://213.142.133.88:8000"; // Son çare (Fallback)

            try
            {
                if (File.Exists(ConfigPath))
                {
                    string json = File.ReadAllText(ConfigPath);
                    using JsonDocument doc = JsonDocument.Parse(json);

                    if (doc.RootElement.TryGetProperty("ServerUrl", out JsonElement urlElement))
                    {
                        string url = urlElement.GetString();
                        if (!string.IsNullOrWhiteSpace(url))
                        {
                            return url.TrimEnd('/');
                        }
                    }
                }
                else
                {
                    Log("HELPERS", "appsettings.json bulunamadı, varsayılan IP kullanılıyor.", true);
                }
            }
            catch (Exception ex)
            {
                Log("HELPERS", $"Config okuma hatası: {ex.Message}", true);
            }

            return defaultUrl;
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