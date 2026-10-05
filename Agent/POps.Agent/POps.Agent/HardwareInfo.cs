using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

#nullable disable

namespace POpsAgent
{
    // WMI donanım sorguları (envanter, dna_payload, yedek kimlik) ve POpsData klasörü ile appsettings.json izinleri
    [SupportedOSPlatform("windows")]
    internal static class HardwareInfo
    {
        // Yalnızca SYSTEM ve Administrators yazabilir; kullanıcı oturumunda çalışan watchdog update.lock'u okuyabilir.
        // Sahibi güvenilir değilse SYSTEM yapılır (bkz. FolderSettings.Secure).
        internal static void SecureDataDirectory(string dir)
        {
            string error = FolderSettings.Secure(dir, usersRead: true, out bool tightened);
            if (error != null) POpsHelpers.Log("AGENT", $"Veri klasörünün ({dir}) izinleri ayarlanamadı: {error}", true);
            else if (tightened) POpsHelpers.Log("AGENT", FolderSettings.TightenedNote(dir, usersRead: true));
        }

        // appsettings.json yalnızca SYSTEM ve Administrators'a açıktır; izin üst klasörden devralınmaz
        // (Program Files, Users'a okuma verir). Kurulum ya da onarım dosyayı varsayılan izinlerle yeniden
        // oluşturabildiği için ACL her açılışta kurulur ve sonuç geri okunarak doğrulanır. Gizli değerler
        // zaten bu dosyada tutulmaz (bkz. AgentCredentials.MigrateSecrets).
        internal static void SecureConfigFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return;
                var file = new FileInfo(path);
                file.SetAccessControl(SecureStore.ProtectedFileSecurity());
                if (!SecureStore.IsLockedDown(file.GetAccessControl()))
                    POpsHelpers.Log("AGENT", $"[GÜVENLİK] {path} kilitlenemedi: SYSTEM/Administrators dışında erişim izni hâlâ var.", true);
            }
            catch (Exception ex) { POpsHelpers.Log("AGENT", $"{path} izinleri ayarlanamadı: {ex.Message}", true); }
        }

        internal static object GetHardwareDnaInternal()
        {
            bool ramReadable = true, diskSerialReal = true, wmiHealthy = true;
            string uuid = "NULL", biosSn = "NULL", diskSn = "NULL", mac = "NULL", ramSn = "NULL";

            try
            {
                // hw.bind özeti aynı normalleştirmeyi kullanır (bkz. HardwareBinding)
                uuid = HardwareBinding.NormalizeUuid(GetWmiValue("Win32_ComputerSystemProduct", "UUID"));
                biosSn = HardwareBinding.NormalizeBiosSerial(GetWmiValue("Win32_BIOS", "SerialNumber"));
                diskSn = GetWmiValue("Win32_DiskDrive", "SerialNumber");
                if (diskSn == "-" || string.IsNullOrWhiteSpace(diskSn)) { diskSerialReal = false; diskSn = GetVolumeId(); }
                ramSn = GetRamSerialNumbers();
                if (ramSn == "NULL") ramReadable = false;
                mac = GetMacAddress();
            }
            catch { wmiHealthy = false; }

            return new
            {
                os = GetWmiValue("Win32_OperatingSystem", "Caption"),
                capabilities = new { ram_readable = ramReadable, disk_serial_real = diskSerialReal, wmi_healthy = wmiHealthy },
                hardware = new { uuid, bios_sn = biosSn, disk_sn = diskSn, mac, ram_sn = ramSn }
            };
        }

        // Takılan bir WMI sağlayıcısı sorguyu süresiz bekletmesin: bağlantı ve her sonuç için zaman aşımı
        private static readonly TimeSpan WmiTimeout = TimeSpan.FromSeconds(15);

        private static ManagementObjectSearcher WmiQuery(string query) =>
            new ManagementObjectSearcher(
                new ManagementScope(@"\\.\root\cimv2", new ConnectionOptions { Timeout = WmiTimeout }),
                new ObjectQuery(query),
                new System.Management.EnumerationOptions { Timeout = WmiTimeout, ReturnImmediately = true, Rewindable = false });

        private static string GetRamSerialNumbers()
        {
            try
            {
                var serials = new List<string>();
                using var searcher = WmiQuery("SELECT SerialNumber FROM Win32_PhysicalMemory");
                foreach (var obj in searcher.Get())
                {
                    string sn = obj["SerialNumber"]?.ToString()?.Trim();
                    if (!string.IsNullOrEmpty(sn) && sn != "Unknown" && sn != "00000000") serials.Add(sn);
                }
                return serials.Count > 0 ? string.Join(",", serials) : "NULL";
            }
            catch { return "NULL"; }
        }

        private static string GetVolumeId()
        {
            try
            {
                var drive = new DriveInfo("C");
                if (drive.IsReady)
                {
                    using var process = new Process();
                    process.StartInfo.FileName = "cmd.exe";
                    process.StartInfo.Arguments = "/c vol c:";
                    process.StartInfo.UseShellExecute = false;
                    process.StartInfo.RedirectStandardOutput = true;
                    process.StartInfo.CreateNoWindow = true;
                    process.Start();
                    string output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit();
                    foreach (string line in output.Split('\n')) if (line.Contains('-')) return line.Split(' ').Last().Trim();
                }
            }
            catch { }
            return "NULL";
        }

        internal static string GenerateFallbackHash()
        {
            try
            {
                string raw = GetWmiValue("Win32_ComputerSystemProduct", "UUID") + GetMacAddress();
                // MD5 güvenlik için değil, kimlik türetmek için: algoritma değişirse kurulu her cihazın kimliği değişirdi
#pragma warning disable CA5351
                byte[] hash = MD5.HashData(Encoding.ASCII.GetBytes(raw));
#pragma warning restore CA5351
                return string.Concat("HW-", Convert.ToHexString(hash).AsSpan(0, 12));
            }
            catch { return string.Concat("HW-", Guid.NewGuid().ToString().AsSpan(0, 12)); }
        }

        internal static string GetWmiValue(string wmiClass, string property)
        {
            try
            {
                using var searcher = WmiQuery($"SELECT {property} FROM {wmiClass}");
                foreach (var obj in searcher.Get()) return obj[property]?.ToString()?.Trim() ?? "-";
            }
            catch { }
            return "-";
        }

        internal static string GetTotalRam()
        {
            try
            {
                using var searcher = WmiQuery("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");
                foreach (var obj in searcher.Get()) if (ulong.TryParse(obj["TotalPhysicalMemory"]?.ToString(), out ulong bytes)) return (bytes / (1024L * 1024 * 1024)) + " GB";
            }
            catch { }
            return "-";
        }

        internal static string GetMacAddress()
        {
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces()) if (nic.OperationalStatus == OperationalStatus.Up && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback) return string.Join(":", nic.GetPhysicalAddress().GetAddressBytes().Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
            }
            catch { }
            return "-";
        }

        internal static string GetLocalIPAddress()
        {
            try
            {
                foreach (var ip in System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName()).AddressList) if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) return ip.ToString();
            }
            catch { }
            return "-";
        }
    }
}
