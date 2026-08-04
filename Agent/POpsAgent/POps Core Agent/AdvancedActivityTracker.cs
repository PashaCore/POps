using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace POpsAgent
{
    [SupportedOSPlatform("windows")]
    public static class AdvancedActivityTracker
    {
        private static CancellationTokenSource _cts;
        private static Task _monitoringTask;
        private static readonly object _lock = new object();
        private static List<ActivityEvent> _eventBuffer = new List<ActivityEvent>();
        private const int MaxBufferSize = 500;
        
        private static AgentPolicy _policy = new AgentPolicy();
        private static string _agentHwId = "";
        private static string _serverUrl = "";
        private static int _dnsViolationsCount = 0;
        private static HashSet<string> _reportedDomains = new HashSet<string>();

        public class ActivityEvent
        {
            public DateTime Timestamp { get; set; }
            public string EventType { get; set; }
            public string Details { get; set; }
        }

        private static List<FileSystemWatcher> _fileWatchers = new List<FileSystemWatcher>();
        private static ManagementEventWatcher _usbWatcher;
        private static ManagementEventWatcher _processStartWatcher;

        private static Dictionary<(int, string, int), DateTime> _lastNetworkSeen = new Dictionary<(int, string, int), DateTime>();
        private static Dictionary<string, DateTime> _lastProcessSeen = new Dictionary<string, DateTime>();

        [DllImport("user32.dll")]
        private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
        private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern uint SetThreadExecutionState(uint esFlags);
        private const uint ES_CONTINUOUS = 0x80000000;
        private const uint ES_SYSTEM_REQUIRED = 0x00000001;

        public static void ConfigurePolicy(AgentPolicy policy, string hwId, string serverUrl)
        {
            lock (_lock)
            {
                _policy = policy;
                _agentHwId = hwId;
                _serverUrl = serverUrl;
            }
        }

        public static void StartAdvancedMonitoring()
        {
            lock (_lock)
            {
                if (_cts != null) return;
                SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED);

                _cts = new CancellationTokenSource();
                _monitoringTask = Task.Run(() => MonitoringLoop(_cts.Token));
                AddEvent("System", "POps Gelişmiş İzleme Motoru Başlatıldı.");
            }
        }

        public static void StopAdvancedMonitoring()
        {
            lock (_lock)
            {
                if (_cts == null) return;
                SetThreadExecutionState(ES_CONTINUOUS);

                _cts.Cancel();
                try { _monitoringTask?.Wait(3000); } catch { }
                _cts = null;
                _monitoringTask = null;
                StopFileWatchers();
                StopWmiWatchers();
            }
        }

        public static void LogTaskProgress(string message)
        {
            AddEvent("TaskProgress", message);
            POpsHelpers.Log("TRACKER", message); // Yerel log dosyasına da yazar
        }

        public static void LogSystemError(string message)
        {
            AddEvent("SystemError", message);
            POpsHelpers.Log("TRACKER", message, true); // Yerel log dosyasına hata olarak yazar
        }

        public static List<ActivityEvent> GetAndClearEvents()
        {
            List<ActivityEvent> snapshot;
            lock (_eventBuffer)
            {
                snapshot = new List<ActivityEvent>(_eventBuffer);
                _eventBuffer.Clear();
            }
            return snapshot;
        }

        private static void AddEvent(string eventType, string details)
        {
            try
            {
                var ev = new ActivityEvent { Timestamp = DateTime.Now, EventType = eventType, Details = details };
                lock (_eventBuffer)
                {
                    _eventBuffer.Add(ev);
                    if (_eventBuffer.Count > MaxBufferSize)
                        _eventBuffer.RemoveRange(0, MaxBufferSize / 2);
                }
            }
            catch { }
        }

        private static void MonitoringLoop(CancellationToken token)
        {
            StartFileWatchers();
            StartWmiWatchers();

            var netTimer = new System.Timers.Timer(15000);
            netTimer.Elapsed += (s, e) => CaptureNetworkConnections();
            netTimer.Start();

            var idleTimer = new System.Timers.Timer(60000);
            idleTimer.Elapsed += (s, e) => CheckUserIdle();
            idleTimer.Start();

            var dnsTimer = new System.Timers.Timer(15000);
            dnsTimer.Elapsed += (s, e) => CheckDnsCacheForViolations();
            dnsTimer.Start();

            while (!token.IsCancellationRequested) { Thread.Sleep(1000); }

            netTimer.Stop();
            idleTimer.Stop();
            dnsTimer.Stop();
        }

        private static void CheckDnsCacheForViolations()
        {
            if (_policy == null || _policy.dns_categories == null || _policy.dns_categories.Count == 0) return;

            try
            {
                var processInfo = new ProcessStartInfo
                {
                    FileName = "ipconfig",
                    Arguments = "/displaydns",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                
                using var process = Process.Start(processInfo);
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();

                var lines = output.Split('\n');
                foreach (var line in lines)
                {
                    if (line.Contains("Record Name", StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = line.Split(':');
                        if (parts.Length > 1)
                        {
                            string domain = parts[1].Trim().ToLower();
                            if (_reportedDomains.Contains(domain)) continue;

                            string matchedCategory = null;

                            if (_policy.dns_categories.Contains("pornografi") && (domain.Contains("porno") || domain.Contains("sex") || domain.Contains("xxx") || domain.Contains("adult")))
                                matchedCategory = "pornografi";
                            else if (_policy.dns_categories.Contains("yasadisi_bahis") && (domain.Contains("bet") || domain.Contains("bahis") || domain.Contains("slot") || domain.Contains("casino")))
                                matchedCategory = "yasadisi_bahis";
                            else if (_policy.dns_categories.Contains("teror_siddet") && (domain.Contains("terror") || domain.Contains("isis") || domain.Contains("silah") || domain.Contains("gore")))
                                matchedCategory = "teror_siddet";
                            else if (_policy.dns_categories.Contains("zararli_yazilim") && (domain.Contains("malware") || domain.Contains("phishing") || domain.Contains("hack") || domain.Contains("exploit")))
                                matchedCategory = "zararli_yazilim";

                            if (matchedCategory != null)
                            {
                                _reportedDomains.Add(domain);
                                AddEvent("DNS_Violation", $"İhlal tespit edildi: {domain} ({matchedCategory})");
                                ReportPolicyViolation(matchedCategory, domain);
                            }
                        }
                    }
                }
            }
            catch { }
        }

        private static void ReportPolicyViolation(string category, string domain)
        {
            if (string.IsNullOrEmpty(_serverUrl) || string.IsNullOrEmpty(_agentHwId)) return;
            
            _dnsViolationsCount++;

            Task.Run(async () =>
            {
                try
                {
                    var payload = new
                    {
                        hw_id = _agentHwId,
                        category = category,
                        domain = domain,
                        action_taken = "Logged",
                        infractions = _dnsViolationsCount
                    };

                    using var http = new System.Net.Http.HttpClient();
                    string json = System.Text.Json.JsonSerializer.Serialize(payload);
                    var content = new System.Net.Http.StringContent(json, System.Text.Encoding.UTF8, "application/json");
                    await http.PostAsync(_serverUrl.TrimEnd('/') + "/api/policy_alert", content);

                    // Eğer karantina limiti aşıldıysa
                    if (_policy.auto_quarantine && _dnsViolationsCount >= _policy.quarantine_threshold)
                    {
                        AddEvent("DNS_Violation", "Karantina eşiği aşıldı. Ağ izole ediliyor!");
                        POpsHelpers.Log("TRACKER", "Karantina limiti aşıldı, izolasyon başlatılıyor.");
                        
                        // ExecuteCommandAsync is inside Worker.cs, but we can trigger it or execute isolation locally
                        TriggerNetworkIsolation();
                    }
                }
                catch { }
            });
        }

        private static void TriggerNetworkIsolation()
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
                
                var processInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-Command \"{psCommand.Replace("\r\n", " ")}\"",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                Process.Start(processInfo);
            }
            catch { }
        }

        private static void StartFileWatchers()
        {
            string[] MonitoredFolders = {
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            };

            foreach (string folder in MonitoredFolders)
            {
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) continue;
                try
                {
                    var watcher = new FileSystemWatcher(folder)
                    {
                        IncludeSubdirectories = true,
                        EnableRaisingEvents = true,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime
                    };
                    watcher.Created += (s, e) => AddEvent("File", $"Yeni Dosya: {Path.GetFileName(e.FullPath)}");
                    watcher.Deleted += (s, e) => AddEvent("File", $"Dosya Silindi: {Path.GetFileName(e.FullPath)}");
                    _fileWatchers.Add(watcher);
                }
                catch { }
            }
        }

        private static void StopFileWatchers()
        {
            foreach (var w in _fileWatchers) { try { w.EnableRaisingEvents = false; w.Dispose(); } catch { } }
            _fileWatchers.Clear();
        }

        private static void StartWmiWatchers()
        {
            try
            {
                var scope = new ManagementScope(@"\\.\root\cimv2");

                _usbWatcher = new ManagementEventWatcher(scope, new WqlEventQuery("SELECT * FROM Win32_DeviceChangeEvent WHERE EventType = 2 OR EventType = 3"));
                _usbWatcher.EventArrived += (s, e) => {
                    int eventType = Convert.ToInt32(e.NewEvent.Properties["EventType"].Value);
                    AddEvent("USB", eventType == 2 ? "Yeni USB Aygıt Takıldı" : "USB Aygıt Çıkarıldı");
                };
                _usbWatcher.Start();

                _processStartWatcher = new ManagementEventWatcher(scope, new WqlEventQuery("SELECT * FROM Win32_ProcessStartTrace"));
                _processStartWatcher.EventArrived += (s, e) => {
                    string pName = e.NewEvent.Properties["ProcessName"].Value.ToString().ToLower();

                    string[] ignored = {
                        "svchost.exe", "conhost.exe", "cmd.exe", "taskhostw.exe", "searchapp.exe",
                        "backgroundtaskhost.exe", "wmiprvse.exe", "dllhost.exe", "dwm.exe",
                        "logonui.exe", "winlogon.exe", "csrss.exe", "smss.exe", "fontdrvhost.exe",
                        "microsoftedgeupdate.exe", "sihost.exe", "explorer.exe", "services.exe",
                        "lsass.exe", "spoolsv.exe", "ctfmon.exe", "runtimebroker.exe", "wermgr.exe",
                        "smartscreen.exe", "taskmgr.exe", "audiodg.exe"
                    };

                    if (!ignored.Contains(pName) && !pName.Contains("pops") && !pName.Contains("popsupdater"))
                    {
                        bool shouldLog = false;
                        lock (_lastProcessSeen)
                        {
                            if (!_lastProcessSeen.ContainsKey(pName) || (DateTime.Now - _lastProcessSeen[pName]).TotalSeconds > 60)
                            {
                                _lastProcessSeen[pName] = DateTime.Now;
                                shouldLog = true;
                            }

                            if (_lastProcessSeen.Count > 100)
                            {
                                var oldKeys = _lastProcessSeen.Where(kvp => (DateTime.Now - kvp.Value).TotalSeconds > 120).Select(kvp => kvp.Key).ToList();
                                foreach (var k in oldKeys) _lastProcessSeen.Remove(k);
                            }
                        }

                        if (shouldLog) AddEvent("AppStart", $"Uygulama Açıldı: {pName}");
                    }
                };
                _processStartWatcher.Start();
            }
            catch { }
        }

        private static void StopWmiWatchers()
        {
            try { _usbWatcher?.Stop(); _usbWatcher?.Dispose(); } catch { }
            try { _processStartWatcher?.Stop(); _processStartWatcher?.Dispose(); } catch { }
        }

        private static void CaptureNetworkConnections()
        {
            try
            {
                var tcpConnections = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections();
                foreach (var conn in tcpConnections)
                {
                    if (conn.State == TcpState.Established && conn.RemoteEndPoint.Port != 0 && conn.RemoteEndPoint.Port != 8000)
                    {
                        if (conn.RemoteEndPoint.Port == 80 || conn.RemoteEndPoint.Port == 443)
                        {
                            var key = (0, conn.RemoteEndPoint.Address.ToString(), conn.RemoteEndPoint.Port);
                            bool shouldLog = false;

                            lock (_lastNetworkSeen)
                            {
                                if (!_lastNetworkSeen.ContainsKey(key))
                                {
                                    _lastNetworkSeen[key] = DateTime.Now;
                                    shouldLog = true;
                                }
                            }

                            if (shouldLog) AddEvent("Network", $"Web İsteği -> {conn.RemoteEndPoint.Address}");
                        }
                    }
                }

                lock (_lastNetworkSeen)
                {
                    var old = _lastNetworkSeen.Where(kvp => (DateTime.Now - kvp.Value).TotalSeconds > 300).ToList();
                    foreach (var o in old) _lastNetworkSeen.Remove(o.Key);
                }
            }
            catch { }
        }

        private static void CheckUserIdle()
        {
            try
            {
                LASTINPUTINFO lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO)) };
                if (GetLastInputInfo(ref lii))
                {
                    uint idleSeconds = ((uint)Environment.TickCount - lii.dwTime) / 1000;
                    if (idleSeconds > 300 && idleSeconds < 360)
                        AddEvent("System", "Bilgisayar 5 dakikadır boşta.");
                }
            }
            catch { }
        }
    }
}