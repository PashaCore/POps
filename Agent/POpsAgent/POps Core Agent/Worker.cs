using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using POpsAgent;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.IO.Pipes;
using System.Linq;
using System.Management;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

#pragma warning disable CA1416
#nullable disable

namespace POpsAgent
{
    public class AgentPolicy
    {
        public string fair_use_text { get; set; } = "";
        public List<string> dns_categories { get; set; } = new List<string>();
        public bool auto_quarantine { get; set; } = false;
        public int quarantine_threshold { get; set; } = 3;
    }

    [SupportedOSPlatform("windows")]
    public class Worker : BackgroundService
    {
        public const string APP_VERSION = "v1.0.0 Stable";

        private readonly ILogger<Worker> _logger;
        private readonly string _pcName;
        private string _hwId;
        private readonly string _identityFilePath = @"C:\POpsData\identity.key";
        private readonly HttpClient _httpClient;

        // 🚀 ARTIK SABİT DEĞİL, HELPERS'TAN OKUNACAK
        private string _serverUrl;

        private ClientWebSocket _commandWs;
        private ClientWebSocket _visionWs;
        private readonly SemaphoreSlim _wsCommandLock = new(1, 1);
        private readonly SemaphoreSlim _wsVisionLock = new(1, 1);

        private TrayPipeServer _trayPipe;
        private volatile bool _isVisionStreamActive;
        private TaskCompletionSource<byte[]> _thumbnailTcs;


        private object _cachedDna = null;
        private object _cachedInventory = null;

        private AgentPolicy _currentPolicy = new AgentPolicy();
        private bool _fairUseAcknowledged = false;
        private int _infractionCount = 0;
        private DateTime _lastPolicyFetch = DateTime.MinValue;

        public Worker(ILogger<Worker> logger)
        {
            string[] args = Environment.GetCommandLineArgs();
            if (args.Contains("POpsV", StringComparer.OrdinalIgnoreCase))
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"\n========================================");
                Console.WriteLine($" POps Agent - Sürüm: {APP_VERSION}");
                Console.WriteLine($"========================================\n");
                Console.ResetColor();
                Environment.Exit(0);
            }

            _logger = logger;
            _pcName = Environment.MachineName;
            _httpClient = new HttpClient();

            // 🚀 IP'Yİ CONFIG DOSYASINDAN AL
            _serverUrl = POpsHelpers.GetServerUrl();
            POpsHelpers.Log("AGENT", $"POps Agent Başlatılıyor (Hedef: {_serverUrl})");

            _cachedDna = GetHardwareDnaInternal();
            _cachedInventory = BuildInventoryInternal();

            _hwId = InitializeIdentity();
            POpsHelpers.Log("AGENT", $"Kimlik Başlatıldı: {_hwId}");
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            string baseWsUrl = _serverUrl.Replace("http://", "ws://").Replace("https://", "wss://");

            // Start background tasks
            _ = Task.Run(() => PolicyPollingLoop(stoppingToken));

            while (!stoppingToken.IsCancellationRequested)
            {
                string commandWsUrl = $"{baseWsUrl}/ws/agent/{_hwId}";
                POpsHelpers.Log("AGENT", $"[POps V4] DUAL-SOCKET MİMARİSİ BAŞLATILDI (v{APP_VERSION})");

                EnsureWatchDogIsRunning();
                StartTrayPipeServer();
                _commandWs = new ClientWebSocket();
                _commandWs.Options.SetRequestHeader("X-Agent-Version", APP_VERSION);

                try
                {
                    await _commandWs.ConnectAsync(new Uri(commandWsUrl), stoppingToken);
                    POpsHelpers.Log("AGENT", "[+] Ana Komut Tüneli Kuruldu.");

                    _ = ReceiveCommandsAsync(_commandWs, stoppingToken);

                    while (_commandWs.State == WebSocketState.Open && !stoppingToken.IsCancellationRequested)
                    {
                        await SendHeartbeatAsync(stoppingToken);
                        await Task.Delay(5000, stoppingToken);
                    }
                }
                catch (Exception ex)
                {
                    POpsHelpers.Log("AGENT", $"[!] Santralle bağlantı koptu: {ex.Message}", true);
                }

                _trayPipe?.Stop();
                await DisconnectVisionTunnelAsync();
                await Task.Delay(5000, stoppingToken);
            }
        }

        private void EnsureWatchDogIsRunning()
        {
            try
            {
                string targetDir = AppDomain.CurrentDomain.BaseDirectory;
                string exePath = Path.Combine(targetDir, "POpsWatchdog.exe");
                if (File.Exists(exePath))
                {
                    // Eğer çalışmıyorsa schtasks ile Session 1'de (BUILTIN\Users) başlat
                    if (Process.GetProcessesByName("POpsWatchdog").Length == 0)
                    {
                        Process p = new Process();
                        p.StartInfo.FileName = "cmd.exe";
                        p.StartInfo.Arguments = $"/c schtasks /create /tn \"POpsWatchdogLauncher\" /tr \"\\\"{exePath}\\\"\" /sc once /st 00:00 /ru \"BUILTIN\\Users\" /it /f >nul 2>&1 & schtasks /run /tn \"POpsWatchdogLauncher\" >nul 2>&1 & schtasks /delete /tn \"POpsWatchdogLauncher\" /f >nul 2>&1";
                        p.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
                        p.StartInfo.CreateNoWindow = true;
                        p.Start();
                        POpsHelpers.Log("AGENT", "POpsWatchDog etkileşimli oturumda (Session 1+) başlatıldı.");
                    }
                }
            }
            catch { }
        }

        private async Task PolicyPollingLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    string apiUrl = _serverUrl.TrimEnd('/') + "/api/agent_policies";
                    string json = await _httpClient.GetStringAsync(apiUrl, token);
                    var policy = JsonSerializer.Deserialize<AgentPolicy>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (policy != null)
                    {
                        _currentPolicy = policy;
                        AdvancedActivityTracker.ConfigurePolicy(policy, _hwId, _serverUrl);
                        
                        if (!string.IsNullOrWhiteSpace(policy.fair_use_text) && !_fairUseAcknowledged)
                        {
                            string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(policy.fair_use_text));
                            _trayPipe?.SendCommandToDesktop($"SHOW_FAIR_USE:{b64}");
                        }
                    }
                }
                catch { }
                await Task.Delay(60000, token); // Poll every minute
            }
        }

        private void StartTrayPipeServer()
        {
            _trayPipe?.Stop();
            _trayPipe = new TrayPipeServer(_logger, _hwId, _httpClient, _serverUrl);

            _trayPipe.OnMessageReceived += (message) =>
            {
                if (message == "USER_COMMAND:PAUSE_WATCHDOG")
                {
                    try { File.WriteAllText(@"C:\POpsData\watchdog_pause.flag", DateTime.Now.ToString()); } catch { }
                    POpsHelpers.Log("AGENT", "Kullanıcı WatchDog'u duraklattı.");
                }
                else if (message == "FAIR_USE_ACK")
                {
                    _fairUseAcknowledged = true;
                    POpsHelpers.Log("AGENT", "Kullanıcı aydınlatma metnini onayladı.");
                }
                else if (message.StartsWith("ACTIVE_WINDOW:"))
                {
                    // İleride active window bilgisini sunucuya heartbeat'e dahil edebiliriz
                }
                else if (message.StartsWith("START_VISION_TUNNEL"))
                {
                    int fps = 2;
                    if (message.Contains(":")) int.TryParse(message.Split(':')[1], out fps);
                    _ = Task.Run(async () => { await ConnectVisionTunnelAsync(CancellationToken.None); _trayPipe?.SendCommandToDesktop($"START_CAPTURE:{fps}"); });
                }
                else if (message.StartsWith("REJECT_VISION_TUNNEL:"))
                {
                    string sessionId = message.Split(':')[1];
                    var payload = new { type = "vision_rejected", session_id = sessionId, hw_id = _hwId };
                    byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
                    _ = Task.Run(async () =>
                    {
                        if (await _wsCommandLock.WaitAsync(2000))
                        {
                            try { if (_commandWs != null && _commandWs.State == WebSocketState.Open) await _commandWs.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None); }
                            finally { _wsCommandLock.Release(); }
                        }
                    });
                }
                else if (message == "STOP_VISION_TUNNEL")
                {
                    _trayPipe?.SendCommandToDesktop("STOP_CAPTURE");
                    _ = DisconnectVisionTunnelAsync();
                }
                else if (message.StartsWith("UNLOCK_BYPASS:"))
                {
                    string token = message.Split(':')[1].Trim();
                    string dateStr = DateTime.Now.ToString("yyyy-MM-dd");
                    string raw = $"{_hwId}OMYO2026{dateStr}";
                    using var sha = System.Security.Cryptography.SHA256.Create();
                    byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
                    string expectedToken = BitConverter.ToString(hash).Replace("-", "").Substring(0, 6).ToUpper();
                    
                    if (token.ToUpper() == expectedToken)
                    {
                        POpsHelpers.Log("AGENT", "Offline Bypass Token DOGRULANDI! Karantina Kaldiriliyor...");
                        _ = DisableNetworkIsolationAsync();
                        _trayPipe?.SendCommandToDesktop("BYPASS_SUCCESS");
                    }
                    else
                    {
                        POpsHelpers.Log("AGENT", "Offline Bypass Token HATALI!");
                        _trayPipe?.SendCommandToDesktop("BYPASS_FAILED");
                    }
                }
            };

            _trayPipe.OnFrameReceived += async (jpegBytes) =>
            {
                var tcs = Interlocked.Exchange(ref _thumbnailTcs, null);
                if (tcs != null)
                {
                    tcs.TrySetResult(jpegBytes);
                    return;
                }
                
                if (!_isVisionStreamActive) return;
                
                var localWs = _visionWs;
                if (localWs == null || localWs.State != WebSocketState.Open) return;
                
                try
                {
                    string base64Image = Convert.ToBase64String(jpegBytes);
                    var framePayload = new { type = "stream_frame", hw_id = _hwId, hostname = _pcName, image = base64Image };
                    byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(framePayload));
                    
                    if (await _wsVisionLock.WaitAsync(1500))
                    {
                        try { await localWs.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None); }
                        finally { _wsVisionLock.Release(); }
                    }
                }
                catch { }
            };

            _trayPipe.Start();
        }

        private async Task ConnectVisionTunnelAsync(CancellationToken token)
        {
            if (_visionWs != null && _visionWs.State == WebSocketState.Open) return;
            string visionWsUrl = _serverUrl.Replace("http://", "ws://").Replace("https://", "wss://") + $"/ws/vision/{_hwId}";
            var newWs = new ClientWebSocket();
            try
            {
                await newWs.ConnectAsync(new Uri(visionWsUrl), token);
                var oldWs = Interlocked.Exchange(ref _visionWs, newWs);
                if (oldWs != null && oldWs.State == WebSocketState.Open) { try { await oldWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "Değişti", CancellationToken.None); } catch { } oldWs.Dispose(); }
                _isVisionStreamActive = true;
                _ = ReceiveVisionInputsAsync(_visionWs, token);
            }
            catch (Exception ex)
            {
                POpsHelpers.Log("AGENT", $"[!] Vision Tüneli açılamadı: {ex.Message}", true);
                _isVisionStreamActive = false;
                newWs.Dispose();
            }
        }

        private async Task DisconnectVisionTunnelAsync()
        {
            _isVisionStreamActive = false;
            var ws = Interlocked.Exchange(ref _visionWs, null);
            if (ws != null) { try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Yayın Kesildi", CancellationToken.None); } catch { } ws.Dispose(); }
        }

        private async Task ReceiveVisionInputsAsync(ClientWebSocket ws, CancellationToken token)
        {
            var buffer = new byte[8192];
            try
            {
                while (ws.State == WebSocketState.Open && _isVisionStreamActive)
                {
                    var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                    string message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    using var doc = JsonDocument.Parse(message);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("type", out var typeProp) && typeProp.GetString() == "remote_input")
                    {
                        string targetDevice = root.GetProperty("device").GetString();
                        if (targetDevice == _hwId) _trayPipe?.SendCommandToDesktop(message);
                    }
                }
            }
            catch { }
            finally { await DisconnectVisionTunnelAsync(); }
        }

        private async Task ReceiveCommandsAsync(ClientWebSocket ws, CancellationToken stoppingToken)
        {
            var buffer = new byte[16384];
            while (ws.State == WebSocketState.Open && !stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), stoppingToken);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                    string message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    using var doc = JsonDocument.Parse(message);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("type", out var typeProp) && typeProp.GetString() == "remote_input")
                    {
                        string targetDevice = root.TryGetProperty("device", out var devProp) ? devProp.GetString() : "";
                        if (targetDevice != _hwId) continue;

                        string act = root.TryGetProperty("action", out var actProp) ? actProp.GetString() : "";
                        if (act == "get_thumbnail")
                        {
                            _ = Task.Run(async () =>
                            {
                                byte[] img = await CaptureSnapshotAsync(TimeSpan.FromSeconds(5));
                                if (img != null && img.Length > 0)
                                {
                                    var payload = new { type = "thumbnail", hw_id = _hwId, image = Convert.ToBase64String(img) };
                                    byte[] b = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
                                    await _wsCommandLock.WaitAsync();
                                    try { if (ws.State == WebSocketState.Open) await ws.SendAsync(new ArraySegment<byte>(b), WebSocketMessageType.Text, true, CancellationToken.None); }
                                    finally { _wsCommandLock.Release(); }
                                }
                            });
                        }
                        else
                        {
                            _trayPipe?.SendCommandToDesktop(message);
                        }
                    }
                    else
                    {
                        string action = root.TryGetProperty("action", out var actionProp) ? actionProp.GetString() : "";
                        if (action == "execute")
                        {
                            string cmd = root.GetProperty("script_path").GetString();
                            int tid = root.GetProperty("task_id").GetInt32();
                            POpsHelpers.Log("AGENT", $"Uzaktan komut çalıştırılıyor (TaskID: {tid})");
                            _ = Task.Run(async () =>
                            {
                                string outpt = await ExecuteCommandAsync(cmd);
                                var res = new { type = "result", pc_name = _hwId, output = outpt, task_id = tid };
                                byte[] b = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(res));
                                await _wsCommandLock.WaitAsync();
                                try { await ws.SendAsync(new ArraySegment<byte>(b), WebSocketMessageType.Text, true, CancellationToken.None); }
                                finally { _wsCommandLock.Release(); }
                            });
                        }
                        else if (action == "get_hardware") await SendHardwareInfoAsync();
                        else if (action == "start_stream") { 
                            await ConnectVisionTunnelAsync(stoppingToken); 
                            int fps = root.TryGetProperty("fps", out var fProp) ? (fProp.ValueKind == JsonValueKind.Number ? fProp.GetInt32() : 2) : 2;
                            _trayPipe?.SendCommandToDesktop($"START_CAPTURE:{fps}"); 
                        }
                        else if (action == "stop_stream") { 
                            _trayPipe?.SendCommandToDesktop("STOP_CAPTURE"); 
                            await DisconnectVisionTunnelAsync(); 
                        }
                        else if (action == "update_agent") 
                        { 
                            string url = root.GetProperty("download_url").GetString();
                            string hash = root.TryGetProperty("hash", out var hProp) ? hProp.GetString() : null;
                            await TriggerAutoUpdateAsync(url, hash); 
                        }
                        else if (action == "wake_peer") { POpsHelpers.SendWolPacket(root.GetProperty("mac").GetString()); }
                        else if (action == "set_identity") { UpdateIdentityFile(root.GetProperty("new_hw_id").GetString()); }
                        else if (action == "lockdown") 
                        { 
                            _trayPipe?.SendCommandToDesktop(message); 
                            await EnableNetworkIsolationAsync(); 
                        }
                        else if (action == "unlock") 
                        { 
                            _trayPipe?.SendCommandToDesktop(message); 
                            await DisableNetworkIsolationAsync(); 
                        }
                        else if (action == "start_vision_session") { _trayPipe?.SendCommandToDesktop(message); }
                    }
                }
                catch { }
            }
        }

        private async Task<byte[]> CaptureSnapshotAsync(TimeSpan timeout)
        {
            if (_trayPipe == null) return null;
            var tcs = new TaskCompletionSource<byte[]>();
            var old = Interlocked.Exchange(ref _thumbnailTcs, tcs);
            old?.TrySetCanceled();
            try
            {
                _trayPipe.SendCommandToDesktop("CAPTURE_SNAPSHOT");
                using var cts = new CancellationTokenSource(timeout);
                cts.Token.Register(() => tcs.TrySetCanceled(), useSynchronizationContext: false);
                return await tcs.Task;
            }
            catch { return null; }
            finally { Interlocked.CompareExchange(ref _thumbnailTcs, null, tcs); }
        }

        private async Task SendHeartbeatAsync(CancellationToken token)
        {
            string currentWindow = "-"; // Servis modunda aktif pencere okunamıyor.

            var statusPayload = new
            {
                hw_id = _hwId,
                hostname = _pcName,
                lab_name = "Atanmamis_Cihazlar",
                status = "Online",
                active_window = currentWindow,
                dna_payload = _cachedDna
            };

            string json = JsonSerializer.Serialize(statusPayload);
            var bytes = Encoding.UTF8.GetBytes(json);

            await _wsCommandLock.WaitAsync(token);
            try { if (_commandWs != null && _commandWs.State == WebSocketState.Open) await _commandWs.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token); }
            finally { _wsCommandLock.Release(); }
        }

        private string InitializeIdentity()
        {
            try
            {
                string dir = Path.GetDirectoryName(_identityFilePath);
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                    try
                    {
                        DirectoryInfo dInfo = new DirectoryInfo(dir);
                        DirectorySecurity sec = dInfo.GetAccessControl();
                        sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                        dInfo.SetAccessControl(sec);
                    }
                    catch { }
                }

                if (File.Exists(_identityFilePath))
                {
                    string savedId = File.ReadAllText(_identityFilePath).Trim();
                    if (!string.IsNullOrEmpty(savedId) && savedId.StartsWith("HW-")) return savedId;
                }

                string newId = GenerateFallbackHash();
                File.WriteAllText(_identityFilePath, newId);
                return newId;
            }
            catch
            {
                return GenerateFallbackHash();
            }
        }

        private void UpdateIdentityFile(string newId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(newId)) return;
                string dir = Path.GetDirectoryName(_identityFilePath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(_identityFilePath, newId);
                _hwId = newId;
                POpsHelpers.Log("AGENT", $"Kimlik başarıyla güncellendi: {_hwId}");
            }
            catch { }
        }

        private object GetHardwareDnaInternal()
        {
            bool ramReadable = true, diskSerialReal = true, wmiHealthy = true;
            string uuid = "NULL", biosSn = "NULL", diskSn = "NULL", mac = "NULL", ramSn = "NULL";

            try
            {
                uuid = GetWmiValue("Win32_ComputerSystemProduct", "UUID");
                if (uuid == "-" || uuid == "FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF") uuid = "NULL";
                biosSn = GetWmiValue("Win32_BIOS", "SerialNumber");
                if (biosSn == "-" || biosSn.Contains("O.E.M")) biosSn = "NULL";
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

        private object BuildInventoryInternal()
        {
            try
            {
                return new
                {
                    hw_id = _hwId,
                    hostname = _pcName,
                    cpu = GetWmiValue("Win32_Processor", "Name"),
                    ram = GetTotalRam(),
                    motherboard = GetWmiValue("Win32_BaseBoard", "Product"),
                    gpu = GetWmiValue("Win32_VideoController", "Name"),
                    os_version = GetWmiValue("Win32_OperatingSystem", "Caption"),
                    ip_address = GetLocalIPAddress(),
                    mac_address = GetMacAddress(),
                    disk_info = GetDiskInfo(),
                    dna = _cachedDna
                };
            }
            catch { return null; }
        }

        private async Task SendHardwareInfoAsync()
        {
            if (_cachedInventory == null) return;
            try
            {
                string json = JsonSerializer.Serialize(_cachedInventory);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                string apiUrl = _serverUrl.TrimEnd('/') + $"/api/inventory/{_hwId}";
                await _httpClient.PostAsync(apiUrl, content);
                POpsHelpers.Log("AGENT", "Donanım envanteri sunucuya gönderildi.");
            }
            catch { }
        }

        private string GetRamSerialNumbers()
        {
            try
            {
                var serials = new List<string>();
                using var searcher = new ManagementObjectSearcher("SELECT SerialNumber FROM Win32_PhysicalMemory");
                foreach (var obj in searcher.Get())
                {
                    string sn = obj["SerialNumber"]?.ToString()?.Trim();
                    if (!string.IsNullOrEmpty(sn) && sn != "Unknown" && sn != "00000000") serials.Add(sn);
                }
                return serials.Count > 0 ? string.Join(",", serials) : "NULL";
            }
            catch { return "NULL"; }
        }

        private string GetVolumeId()
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
                    foreach (string line in output.Split('\n')) if (line.Contains("-")) return line.Split(' ').Last().Trim();
                }
            }
            catch { }
            return "NULL";
        }

        private string GenerateFallbackHash()
        {
            try
            {
                string raw = GetWmiValue("Win32_ComputerSystemProduct", "UUID") + GetMacAddress();
                using MD5 md5 = MD5.Create();
                byte[] hash = md5.ComputeHash(Encoding.ASCII.GetBytes(raw));
                return "HW-" + BitConverter.ToString(hash).Replace("-", "").Substring(0, 12);
            }
            catch { return "HW-" + Guid.NewGuid().ToString().Substring(0, 12); }
        }

        private string GetWmiValue(string wmiClass, string property)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher($"SELECT {property} FROM {wmiClass}");
                foreach (var obj in searcher.Get()) return obj[property]?.ToString()?.Trim() ?? "-";
            }
            catch { }
            return "-";
        }

        private string GetTotalRam()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");
                foreach (var obj in searcher.Get()) if (ulong.TryParse(obj["TotalPhysicalMemory"]?.ToString(), out ulong bytes)) return (bytes / (1024L * 1024 * 1024)) + " GB";
            }
            catch { }
            return "-";
        }

        private string GetMacAddress()
        {
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces()) if (nic.OperationalStatus == OperationalStatus.Up && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback) return string.Join(":", nic.GetPhysicalAddress().GetAddressBytes().Select(b => b.ToString("X2")));
            }
            catch { }
            return "-";
        }

        private string GetLocalIPAddress()
        {
            try
            {
                foreach (var ip in System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName()).AddressList) if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) return ip.ToString();
            }
            catch { }
            return "-";
        }

        private string GetDiskInfo()
        {
            try
            {
                var sb = new StringBuilder();
                foreach (var drive in DriveInfo.GetDrives()) if (drive.IsReady && drive.DriveType == DriveType.Fixed) sb.Append($"{drive.Name} {drive.TotalFreeSpace / (1024L * 1024 * 1024)}GB Boş / {drive.TotalSize / (1024L * 1024 * 1024)}GB Toplam | ");
                return sb.ToString().TrimEnd(' ', '|');
            }
            catch { }
            return "-";
        }

        private async Task TriggerAutoUpdateAsync(string downloadUrl, string expectedHash = null)
        {
            try
            {
                POpsHelpers.Log("AGENT", $"Güncelleme başlatıldı. İndiriliyor: {downloadUrl}");
                string targetDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
                string tmpDir = @"C:\.pops_tmp";
                string zipPath = Path.Combine(Path.GetTempPath(), "update.zip");

                foreach (var p in Process.GetProcessesByName("POpsWatchdog")) { try { p.Kill(); } catch { } }
                foreach (var p in Process.GetProcessesByName("POpsVision")) { try { p.Kill(); } catch { } }
                await Task.Delay(1000);

                if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
                Directory.CreateDirectory(tmpDir);

                byte[] fileBytes = await _httpClient.GetByteArrayAsync(downloadUrl);
                
                if (!string.IsNullOrEmpty(expectedHash))
                {
                    using (var sha256 = SHA256.Create())
                    {
                        byte[] hashBytes = sha256.ComputeHash(fileBytes);
                        string computedHash = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
                        if (computedHash != expectedHash.ToLowerInvariant())
                        {
                            POpsHelpers.Log("AGENT", $"[GÜVENLİK] İndirilen paketin bütünlük doğrulaması (Hash Mismatch) başarısız oldu. Beklenen: {expectedHash}, Hesaplanan: {computedHash}");
                            return; // Abort update
                        }
                        POpsHelpers.Log("AGENT", "[GÜVENLİK] Paket bütünlüğü doğrulandı (SHA256 eşleşti).");
                    }
                }

                await File.WriteAllBytesAsync(zipPath, fileBytes);
                ZipFile.ExtractToDirectory(zipPath, tmpDir);

                string updaterPath = Path.Combine(tmpDir, "POpsUpdater.exe");
                if (!File.Exists(updaterPath)) return;

                string batPath = Path.Combine(tmpDir, "apply_update.bat");

                string batContent = $@"@echo off
sc stop POpsAgent >nul 2>&1
timeout /t 3 /nobreak >nul
taskkill /F /IM POpsWatchdog.exe >nul 2>&1
taskkill /F /IM POpsVision.exe >nul 2>&1
taskkill /F /IM POpsAgent.exe >nul 2>&1
timeout /t 2 /nobreak >nul
""{updaterPath}"" ""{targetDir}""

schtasks /create /tn ""POpsWatchdogLauncher"" /tr ""\""{targetDir}\POpsWatchdog.exe\"""" /sc once /st 00:00 /ru ""BUILTIN\Users"" /it /f >nul 2>&1
schtasks /run /tn ""POpsWatchdogLauncher"" >nul 2>&1
schtasks /delete /tn ""POpsWatchdogLauncher"" /f >nul 2>&1

del ""%~f0""";

                File.WriteAllText(batPath, batContent);
                Process.Start(new ProcessStartInfo { FileName = batPath, UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
            }
            catch (Exception ex)
            {
                POpsHelpers.Log("AGENT", $"Güncelleme Hatası: {ex.Message}", true);
            }
        }

        private async Task EnableNetworkIsolationAsync()
        {
            try
            {
                Uri serverUri = new Uri(_serverUrl);
                string serverIp = serverUri.Host;

                string psCommand = $@"
                    New-NetFirewallRule -DisplayName 'POps_Isolation_BlockOut' -Direction Outbound -Action Block -Profile Any
                    New-NetFirewallRule -DisplayName 'POps_Isolation_BlockIn' -Direction Inbound -Action Block -Profile Any
                    New-NetFirewallRule -DisplayName 'POps_Isolation_AllowServerOut' -Direction Outbound -Action Allow -RemoteAddress {serverIp} -Profile Any
                    New-NetFirewallRule -DisplayName 'POps_Isolation_AllowServerIn' -Direction Inbound -Action Allow -RemoteAddress {serverIp} -Profile Any
                ";
                await ExecuteCommandAsync($"powershell -Command \"{psCommand.Replace("\r\n", " ")}\"");
                POpsHelpers.Log("AGENT", "AG IZOLASYONU AKTIF EDILDI!");
            }
            catch (Exception ex) { POpsHelpers.Log("AGENT", $"Ag Izolasyonu basarisiz: {ex.Message}", true); }
        }

        private async Task DisableNetworkIsolationAsync()
        {
            try
            {
                string psCommand = $@"
                    Remove-NetFirewallRule -DisplayName 'POps_Isolation_BlockOut' -ErrorAction SilentlyContinue
                    Remove-NetFirewallRule -DisplayName 'POps_Isolation_BlockIn' -ErrorAction SilentlyContinue
                    Remove-NetFirewallRule -DisplayName 'POps_Isolation_AllowServerOut' -ErrorAction SilentlyContinue
                    Remove-NetFirewallRule -DisplayName 'POps_Isolation_AllowServerIn' -ErrorAction SilentlyContinue
                ";
                await ExecuteCommandAsync($"powershell -Command \"{psCommand.Replace("\r\n", " ")}\"");
                POpsHelpers.Log("AGENT", "AG IZOLASYONU KALDIRILDI!");
            }
            catch (Exception ex) { POpsHelpers.Log("AGENT", $"Ag Izolasyonu kaldirilamadi: {ex.Message}", true); }
        }

        private async Task<string> ExecuteCommandAsync(string command)
        {
            string tempBatPath = "";
            try
            {
                tempBatPath = Path.Combine(Path.GetTempPath(), $"pops_task_{Guid.NewGuid():N}.bat");
                await File.WriteAllTextAsync(tempBatPath, "@echo off\r\nchcp 65001 > nul\r\n" + command, new UTF8Encoding(false));

                var processInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c \"{tempBatPath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                using var process = new Process { StartInfo = processInfo };
                var outputBuilder = new StringBuilder();
                var errorBuilder = new StringBuilder();

                process.OutputDataReceived += (s, e) => { if (e.Data != null) outputBuilder.AppendLine(e.Data); };
                process.ErrorDataReceived += (s, e) => { if (e.Data != null) errorBuilder.AppendLine(e.Data); };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
                try { await process.WaitForExitAsync(cts.Token); }
                catch (TaskCanceledException)
                {
                    try { process.Kill(true); } catch { }
                    return "[HATA]: İşlem 30 dakikadan uzun sürdüğü için zorla sonlandırıldı.";
                }

                string stdOut = outputBuilder.ToString().Trim();
                string stdErr = errorBuilder.ToString().Trim();
                try { File.Delete(tempBatPath); } catch { }

                if (process.ExitCode != 0 && !string.IsNullOrWhiteSpace(stdErr))
                    return $"[ÇIKIŞ KODU: {process.ExitCode}]\n[HATA]:\n{stdErr}\n[ÇIKTI]:\n{stdOut}";

                return string.IsNullOrWhiteSpace(stdOut) ? $"Komut çalıştı (Çıkış: {process.ExitCode}) ancak çıktı üretilmedi." : stdOut;
            }
            catch (Exception ex)
            {
                if (!string.IsNullOrEmpty(tempBatPath) && File.Exists(tempBatPath)) try { File.Delete(tempBatPath); } catch { }
                return $"Ajan Hatası: {ex.Message}";
            }
        }

    }
    public class TrayPipeServer
    {
        private readonly ILogger _logger;
        private readonly string _hwId;
        private readonly HttpClient _http;
        private readonly string _serverUrl;
        private CancellationTokenSource _cts;
        private NamedPipeServerStream _pipeServer;
        public event Action<string> OnMessageReceived = delegate { };
        public event Action<byte[]> OnFrameReceived = delegate { };

        public TrayPipeServer(ILogger logger, string hwId, HttpClient http, string serverUrl)
        {
            _logger = logger; _hwId = hwId; _http = http; _serverUrl = serverUrl;
        }

        public void Start() { _cts = new CancellationTokenSource(); Task.Run(() => ListenPipeAsync(_cts.Token)); }
        public void Stop() { _cts?.Cancel(); _pipeServer?.Dispose(); }

        public void SendCommandToDesktop(string json)
        {
            if (_pipeServer == null || !_pipeServer.IsConnected) return;
            try
            {
                byte[] b = Encoding.UTF8.GetBytes(json);
                byte[] len = BitConverter.GetBytes(b.Length);
                _pipeServer.Write(len, 0, 4);
                _pipeServer.Write(b, 0, b.Length);
                _pipeServer.Flush();
            }
            catch { }
        }

        private async Task ListenPipeAsync(CancellationToken token)
        {
            string pipeName = @"POpsTrayPipe";
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var ps = new PipeSecurity();
                    var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
                    var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
                    ps.AddAccessRule(new PipeAccessRule(admins, PipeAccessRights.FullControl, AccessControlType.Allow));
                    ps.AddAccessRule(new PipeAccessRule(everyone, PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));

                    _pipeServer = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, ps);
                    POpsHelpers.Log("PIPE", $"Bekleniyor: {pipeName}");

                    await _pipeServer.WaitForConnectionAsync(token);
                    POpsHelpers.Log("PIPE", "🟢 Vision bağlandı!");

                    byte[] lBuf = new byte[4];
                    while (_pipeServer.IsConnected && !token.IsCancellationRequested)
                    {
                        int lRead = 0;
                        while (lRead < 4)
                        {
                            int r = await _pipeServer.ReadAsync(lBuf, lRead, 4 - lRead, token);
                            if (r == 0) break;
                            lRead += r;
                        }
                        if (lRead < 4) break;
                        
                        int dLen = BitConverter.ToInt32(lBuf, 0);
                        byte[] d = new byte[dLen];
                        int total = 0;
                        while (total < dLen)
                        {
                            int r = await _pipeServer.ReadAsync(d, total, dLen - total, token);
                            if (r == 0) break;
                            total += r;
                        }
                        if (total == dLen) 
                        {
                            if (dLen > 2 && d[0] == 0xFF && d[1] == 0xD8)
                            {
                                OnFrameReceived?.Invoke(d);
                            }
                            else
                            {
                                string msg = Encoding.UTF8.GetString(d);
                                OnMessageReceived?.Invoke(msg);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    POpsHelpers.Log("PIPE", $"Hata: {ex.Message}", true);
                    await Task.Delay(3000, token);
                }
                finally { _pipeServer?.Dispose(); }
            }
        }
    }
}
