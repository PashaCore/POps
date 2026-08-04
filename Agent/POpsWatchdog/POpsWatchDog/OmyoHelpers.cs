using System;
using System.IO;
using System.Text.Json;

namespace POpsWatchDog // Hangi projedeysen namespace'i ona göre uyarlayabilirsin (örn: POps.Agent)
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
            string defaultUrl = "http://127.0.0.1:8000"; // Son çare (Fallback)

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
    }
}