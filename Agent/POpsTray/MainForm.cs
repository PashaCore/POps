using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;
using System.IO.Pipes;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.IO;
using System.Diagnostics;

namespace POpsTray
{
    public partial class MainForm : Form
    {
        private NotifyIcon trayIcon;
        private ContextMenuStrip trayMenu;
        private NamedPipeClientStream? pipeClient;
        private CancellationTokenSource cts = new CancellationTokenSource();
        // Sunucuya son bildirilen ön plan uygulaması (yalnızca süreç adı)
        private string lastApp = "";
        private KioskForm _activeKioskForm;
        // Ekran önizlemesi bildirimleri: her önizleme ipucu metnine yazılır, balon en fazla 5 dakikada bir çıkar
        private DateTime _lastPreviewBalloon = DateTime.MinValue;
        private static readonly TimeSpan PreviewBalloonInterval = TimeSpan.FromMinutes(5);
        private CancellationTokenSource? _captureCts;
        private readonly object _pipeLock = new object();
        // Yardım masası pencereleri (tek kopya) ve son balonun bir talep yanıtı olup olmadığı
        private ReportProblemForm? _reportForm;
        private MyTicketsForm? _ticketsForm;
        private ActivityForm? _activityForm;
        private bool _lastBalloonIsTicket;

        // UIPI gerektirmeyen, doğrudan User Session'da çalışan API'ler
        [DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        public static extern bool LockWorkStation();

        [DllImport("user32.dll")]
        static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, int dwExtraInfo);

        // Uzaktan klavye: SendInput (keybd_event'in yerine; KEYEVENTF_UNICODE ile düzenden bağımsız karakter)
        [DllImport("user32.dll", SetLastError = true)]
        static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        static extern IntPtr GetKeyboardLayout(uint idThread);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern short VkKeyScanEx(char ch, IntPtr dwhkl);

        [DllImport("user32.dll")]
        static extern uint MapVirtualKey(uint uCode, uint uMapType);

        [StructLayout(LayoutKind.Sequential)]
        struct INPUT
        {
            public uint type;
            public InputUnion U;
        }

        // Birlik en büyük üyesi (MOUSEINPUT) kadar olmalı: SendInput cbSize'ı denetler
        [StructLayout(LayoutKind.Explicit)]
        struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MOUSEINPUT
        {
            public int dx, dy;
            public uint mouseData, dwFlags, time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct KEYBDINPUT
        {
            public ushort wVk, wScan;
            public uint dwFlags, time;
            public IntPtr dwExtraInfo;
        }

        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint KEYEVENTF_UNICODE = 0x0004;

        // Panelden basılıp henüz bırakılmamış sanal tuşlar (oturum bitince bırakılır)
        private readonly POps.Shared.PressedKeys _pressedKeys = new POps.Shared.PressedKeys();

        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;
        private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
        private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
        private const uint MOUSEEVENTF_WHEEL = 0x0800;

        // Yardım masası menüleri: sunucuda modül kapalıysa servis gizletir (HELPDESK_MENU)
        private readonly ToolStripMenuItem _reportItem;
        private readonly ToolStripMenuItem _ticketsItem;

        public MainForm()
        {
            this.ShowInTaskbar = false;
            this.WindowState = FormWindowState.Minimized;
            this.FormBorderStyle = FormBorderStyle.FixedToolWindow;
            this.Opacity = 0;

            // Öğrenci menüsünde tepsiyi kapatan, watchdog'u duraklatan veya yerine getirilmeyen
            // "izlemeyi duraklat" seçenekleri yoktur; kiosk ve rıza pencereleri tepsiyle birlikte kapanırdı.
            trayMenu = new ContextMenuStrip();
            _reportItem = new ToolStripMenuItem("Sorun bildir", null, (_, _) => OpenReportForm());
            _ticketsItem = new ToolStripMenuItem("Taleplerim", null, (_, _) => OpenTicketsForm());
            trayMenu.Items.Add(_reportItem);
            trayMenu.Items.Add(_ticketsItem);
            trayMenu.Items.Add(new ToolStripMenuItem("Etkinlik geçmişim", null, (_, _) => OpenActivityForm()));
            trayMenu.Items.Add("-");
            trayMenu.Items.Add(new ToolStripMenuItem("Hakkında", null, OnAboutClicked));
            trayMenu.Items.Add("-");
            var adminItem = new ToolStripMenuItem("Yönetici Müdahalesi (Bypass)", null, OnAdminBypassClicked);
            adminItem.ForeColor = Color.Red;
            trayMenu.Items.Add(adminItem);

            trayIcon = new NotifyIcon();
            trayIcon.Text = "POps - Ajanı";
            trayIcon.Icon = SystemIcons.Shield; // İleride özel bir .ico dosyası yüklenebilir
            trayIcon.ContextMenuStrip = trayMenu;
            trayIcon.Visible = true;
            trayIcon.BalloonTipClicked += (_, _) => { if (_lastBalloonIsTicket) OpenTicketsForm(); };

            _ = Task.Run(() => ConnectToServiceAsync(cts.Token));
            _ = Task.Run(() => MonitorActiveAppAsync(cts.Token));
        }

        public void ShowNotification(string title, string message)
        {
            _lastBalloonIsTicket = false;
            trayIcon.BalloonTipTitle = title;
            trayIcon.BalloonTipText = message;
            trayIcon.BalloonTipIcon = ToolTipIcon.Info;
            trayIcon.ShowBalloonTip(3000);
        }

        protected override void OnLoad(EventArgs e)
        {
            this.Visible = false;
            base.OnLoad(e);
            
            // Kullanıcıya periyodik izleme yapıldığına dair yasal/kurumsal uyarı
            ShowNotification("POps - Ajanı", "Bu cihaz kurumsal güvenlik politikaları gereği izlenmektedir.\nEkran etkinlikleriniz periyodik olarak arka planda kaydedilebilir.");
        }

        private async Task ConnectToServiceAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    // Şimdilik test amaçlı sabit bir isim, ileride hwId ile dinamik olacak
                    string pipeName = @"POpsTrayPipe"; 
                    pipeClient = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                    
                    await pipeClient.ConnectAsync(5000, token);
                    // Boru servisin mi? Sahibi SYSTEM ya da Administrators olmalı (bkz. POps.Shared.PipeOwner)
                    if (!POps.Shared.PipeOwner.Check(pipeClient, out string owner))
                    {
                        TrayLog.Write($"[GÜVENLİK] POpsTrayPipe servise ait değil (sahip: {owner}); bağlantı kesildi.");
                        pipeClient.Dispose();
                        await Task.Delay(10000, token);
                        continue;
                    }
                    // Servis yeniden bağlanınca uygulama adını bilmez; bir sonraki turda yeniden gönderilir
                    lastApp = "";
                    
                    // Bağlantı başarılı, dinlemeye başla
                    byte[] lBuf = new byte[4];
                    while (pipeClient.IsConnected && !token.IsCancellationRequested)
                    {
                        int lRead = 0;
                        while (lRead < 4)
                        {
                            int r = await pipeClient.ReadAsync(lBuf, lRead, 4 - lRead, token);
                            if (r == 0) break;
                            lRead += r;
                        }
                        if (lRead < 4) break;
                        
                        int dLen = BitConverter.ToInt32(lBuf, 0);
                        if (dLen <= 0 || dLen > 10 * 1024 * 1024) break; // Güvenlik kontrolü

                        byte[] d = new byte[dLen];
                        int total = 0;
                        while (total < dLen)
                        {
                            int r = await pipeClient.ReadAsync(d, total, dLen - total, token);
                            if (r == 0) break;
                            total += r;
                        }
                        
                        if (total == dLen)
                        {
                            string msg = Encoding.UTF8.GetString(d);
                            ProcessMessageFromService(msg);
                        }
                    }
                }
                catch
                {
                    // Bağlantı koptuysa veya yoksa 3 saniye bekle tekrar dene
                    await Task.Delay(3000, token);
                }
                finally
                {
                    pipeClient?.Dispose();
                    // Servis bağlantısı koptu: uzaktan basılmış tuş kalmasın
                    ReleasePressedKeys();
                }
            }
        }

        private void ProcessMessageFromService(string jsonMsg)
        {
            try
            {
                // Yalnızca mesaj türü; içerik (özellikle uzaktan tuş/fare olayları) loglanmaz
                string kind = TrayLog.Describe(jsonMsg);
                if (!kind.StartsWith("type=remote_input")) TrayLog.Write($"Alındı: {kind}");

                if (jsonMsg.StartsWith("START_CAPTURE")) 
                { 
                    int fps = 2;
                    if (jsonMsg.Contains(":")) int.TryParse(jsonMsg.Split(':')[1], out fps);
                    StartCaptureLoop(fps); 
                    return; 
                }
                if (jsonMsg.Contains("STOP_CAPTURE")) { StopCaptureLoop(); ReleasePressedKeys(); return; }
                if (jsonMsg.Contains("CAPTURE_SNAPSHOT")) { SendSnapshot(); NotifyPreviewTaken(); return; }

                // Çevrimdışı bypass kodunun sonucu. Kabul edilirse servis ayrıca "unlock" gönderir (kilit ekranı kapanır).
                if (jsonMsg == "BYPASS_FAILED")
                {
                    this.Invoke(new Action(() =>
                    {
                        if (_activeKioskForm != null && !_activeKioskForm.IsDisposed) _activeKioskForm.ShowBypassRejected();
                        else ShowNotification("Bypass", "Bypass kodu kabul edilmedi.");
                    }));
                    return;
                }
                if (jsonMsg == "BYPASS_SUCCESS") return;
                // Yardım masası modülü (sunucuda laboratuvar bazında): kapalıyken talep menüleri gizlenir, açılınca geri gelir
                if (jsonMsg.StartsWith("HELPDESK_MENU:"))
                {
                    bool visible = jsonMsg != "HELPDESK_MENU:0";
                    this.Invoke(new Action(() =>
                    {
                        _reportItem.Visible = visible;
                        _ticketsItem.Visible = visible;
                    }));
                    return;
                }
                if (jsonMsg.StartsWith("TICKET_RESULT:") || jsonMsg.StartsWith("TICKET_LIST_RESULT:") || jsonMsg.StartsWith("TICKET_NOTIFY:") || jsonMsg.StartsWith("ACTIVITY_LIST_RESULT:"))
                {
                    HandleHelpdeskMessage(jsonMsg);
                    return;
                }
                // Karantina kaldırılamadı (ağ yalıtımı duruyor): kilit ekranı açık kalır, kullanıcıya "kaldırıldı" denmez
                if (jsonMsg == "UNLOCK_FAILED")
                {
                    this.Invoke(new Action(() =>
                    {
                        if (_activeKioskForm != null && !_activeKioskForm.IsDisposed) _activeKioskForm.ShowUnlockFailed();
                        ShowNotification("Karantina kaldırılamadı", "Ağ yalıtımı kaldırılamadı; cihaz kilitli kalıyor. BT yöneticisine bildirin.");
                    }));
                    return;
                }
                
                if (jsonMsg.StartsWith("SHOW_FAIR_USE:"))
                {
                    string b64 = jsonMsg.Substring("SHOW_FAIR_USE:".Length);
                    string content = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
                    this.Invoke(new Action(() => 
                    {
                        var form = new FairUseForm(content, () => SendToService("FAIR_USE_ACK"));
                        form.Show();
                    }));
                    return;
                }

                // Artık temiz JSON geldiğinden indexOf workaround'a gerek yok.
                // string cleanJson = jsonMsg.Substring(startIndex);

                using var doc = JsonDocument.Parse(jsonMsg);
                var root = doc.RootElement;

                if (root.TryGetProperty("type", out var typeProp) && typeProp.GetString() == "remote_input")
                {
                    ProcessRemoteInput(root);
                    return;
                }

                if (root.TryGetProperty("action", out var actProp))
                {
                    string action = actProp.GetString();
                    if (action == "lockdown")
                    {
                        string reason = root.TryGetProperty("reason", out var r) ? r.GetString() : "Bilinmiyor";
                        // Servis kilit sürdükçe tepsi her bağlandığında bunu yeniden gönderir; kilit ekranı zaten açıksa
                        // yeni pencere ve bildirim yok
                        this.Invoke(new Action(() =>
                        {
                            if (_activeKioskForm == null || _activeKioskForm.IsDisposed)
                            {
                                _activeKioskForm = new KioskForm(reason ?? "Belirtilmedi", code => SendToService($"UNLOCK_BYPASS:{code}"));
                                _activeKioskForm.Show();
                                ShowNotification("Acil Durum İzolasyonu", $"Cihaz BT tarafından kilitlendi!\nNeden: {reason}");
                            }
                        }));
                    }
                    else if (action == "unlock")
                    {
                        // source: server (panel), bypass (çevrimdışı kod), sync (tepsi bağlandı ve kilit yok: açık kalmış
                        // eski kilit ekranı kapanır, kilit ekranı yoksa bildirim de yok)
                        string source = root.TryGetProperty("source", out var src) && src.ValueKind == JsonValueKind.String ? src.GetString() ?? "" : "";
                        this.Invoke(new Action(() =>
                        {
                            bool wasLocked = _activeKioskForm != null && !_activeKioskForm.IsDisposed;
                            if (wasLocked)
                            {
                                _activeKioskForm!.AllowClose = true;
                                _activeKioskForm.Hide();
                                _activeKioskForm.Close();
                                _activeKioskForm = null;
                            }
                            if (source == "sync" && !wasLocked) return;
                            ShowNotification("Karantina Kaldırıldı", source == "bypass"
                                ? "Çevrimdışı bypass kodu kabul edildi; kilit ekranı ve ağ yalıtımı kaldırıldı."
                                : "Cihazın karantina durumu sistem yöneticisi tarafından kaldırıldı.");
                        }));
                    }
                    else if (action == "start_vision_session")
                    {
                        bool isMandatory = root.GetProperty("is_mandatory").GetBoolean();
                        string adminName = root.GetProperty("admin_name").GetString();
                        string sessionId = root.GetProperty("session_id").GetString();
                        string reason = root.TryGetProperty("reason", out var rea) ? rea.GetString() : "";
                        int fps = root.TryGetProperty("fps", out var fpsProp) ? (fpsProp.ValueKind == JsonValueKind.Number ? fpsProp.GetInt32() : 2) : 2;
                        int countdownSec = root.TryGetProperty("countdown_seconds", out var cdProp) ? (cdProp.ValueKind == JsonValueKind.Number ? cdProp.GetInt32() : 0) : 0;
                        bool isQuarantined = root.TryGetProperty("is_quarantined", out var iqProp) && iqProp.ValueKind == JsonValueKind.True;
                        
                        this.Invoke(new Action(() => 
                        {
                            if (isMandatory)
                            {
                                if (countdownSec > 0)
                                {
                                    CountdownForm cf = new CountdownForm(countdownSec, reason, isQuarantined, () => 
                                    {
                                        ShowNotification("Kurumsal Bildirim", $"Bilgi İşlem yetkilisi {adminName} cihazınıza bağlandı.\nİşlem No: {sessionId}");
                                        SendToService($"START_VISION_TUNNEL:{fps}");
                                    });
                                    cf.Show();
                                }
                                else
                                {
                                    ShowNotification("Kurumsal Bildirim", $"Bilgi İşlem yetkilisi {adminName} bakım/güvenlik amacıyla bu cihaza uzaktan bağlanacaktır.\nİşlem No: {sessionId}\nBu işlem kayıt altına alınacaktır.");
                                    SendToService($"START_VISION_TUNNEL:{fps}");
                                }
                            }
                            else
                            {
                                ShowNotification("Bağlantı İsteği", $"Sistem yöneticisi {adminName} ekranınıza bağlanmak istiyor.");
                                
                                DialogResult res;
                                using (Form topmostForm = new Form { Size = new Size(1,1), StartPosition = FormStartPosition.Manual, Location = new Point(-2000, -2000), TopMost = true, ShowInTaskbar = false })
                                {
                                    topmostForm.Show();
                                    res = MessageBox.Show(
                                        topmostForm,
                                        $"Bilgi İşlem Yetkilisi ({adminName}) ekranınıza bağlanmak istiyor.\nGerekçe: {reason}\n\nKabul ediyor musunuz?",
                                        "POps Uzaktan Destek",
                                        MessageBoxButtons.YesNo,
                                        MessageBoxIcon.Question,
                                        MessageBoxDefaultButton.Button1
                                    );
                                }

                                if (res == DialogResult.Yes)
                                {
                                    SendToService($"START_VISION_TUNNEL:{fps}");
                                }
                                else
                                {
                                    SendToService($"REJECT_VISION_TUNNEL:{sessionId}");
                                }
                            }
                        }));
                    }
                    else if (action == "get_thumbnail")
                    {
                        SendSnapshot();
                        NotifyPreviewTaken();
                    }
                    else if (action == "stop_stream")
                    {
                        StopCaptureLoop();
                    }
                }
            }
            catch (Exception ex)
            {
                TrayLog.Write($"Hata ({TrayLog.Describe(jsonMsg)}): {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void ProcessRemoteInput(JsonElement root)
        {
            try
            {
                // Panelin FPS seçicisi {"action":"set_fps","fps":N} gönderir; eskiden input_type olmadığı için atılıyordu
                if (root.TryGetProperty("action", out var actionProp) && actionProp.GetString() == "set_fps")
                {
                    int fps = root.TryGetProperty("fps", out var fpsProp) && fpsProp.ValueKind == JsonValueKind.Number ? fpsProp.GetInt32() : 2;
                    ChangeCaptureFps(fps);
                    return;
                }

                if (!root.TryGetProperty("input_type", out var typeProp)) return;
                string inputType = typeProp.GetString();

                if (inputType == "mouse_move")
                {
                    int x = root.GetProperty("x").GetInt32();
                    int y = root.GetProperty("y").GetInt32();
                    SetCursorPos(x, y);
                }
                else if (inputType == "mouse_click")
                {
                    string button = root.GetProperty("button").GetString();
                    bool isDown = root.GetProperty("is_down").GetBoolean();
                    uint flag = 0;
                    if (button == "left") flag = isDown ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP;
                    else if (button == "right") flag = isDown ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP;
                    else if (button == "middle") flag = isDown ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP;
                    if (flag != 0) mouse_event(flag, 0, 0, 0, 0);
                }
                else if (inputType == "mouse_wheel")
                {
                    int delta = root.GetProperty("delta").GetInt32();
                    mouse_event(MOUSEEVENTF_WHEEL, 0, 0, unchecked((uint)delta), 0);
                }
                else if (inputType == "keyboard")
                {
                    string key = root.TryGetProperty("key", out var keyProp) && keyProp.ValueKind == JsonValueKind.String ? keyProp.GetString() ?? "" : "";
                    string? code = root.TryGetProperty("code", out var codeProp) && codeProp.ValueKind == JsonValueKind.String ? codeProp.GetString() : null;
                    bool isDown = root.GetProperty("is_down").GetBoolean();
                    bool Flag(string name) => root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.True;
                    var mods = new POps.Shared.RemoteKeyModifiers { Ctrl = Flag("ctrl"), Alt = Flag("alt"), Shift = Flag("shift"), Meta = Flag("meta"), AltGr = Flag("altgr") };

                    POps.Shared.RemoteKeyStroke? stroke = POps.Shared.RemoteKeyMap.Map(key, code, mods, ForegroundVkKeyScan);
                    if (stroke == null) return;
                    _pressedKeys.Track(stroke, isDown);
                    SendKeyStroke(stroke, isDown);
                }
            }
            catch (Exception ex)
            {
                TrayLog.Write($"Uzaktan girdi uygulanamadı: {ex.GetType().Name}");
            }
        }

        // Kısayol tuşunun sanal tuşu ön plandaki pencerenin klavye düzenine göre (ör. Türkçe Q'da "ç")
        private static short ForegroundVkKeyScan(char c)
        {
            uint thread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
            return VkKeyScanEx(c, GetKeyboardLayout(thread));
        }

        private static void SendKeyStroke(POps.Shared.RemoteKeyStroke stroke, bool isDown)
        {
            INPUT[] inputs;
            if (stroke.IsUnicode)
            {
                // Her UTF-16 birimi ayrı olay (vekil çift: iki olay); basma ve bırakma ayrı mesajlarla gelir
                inputs = stroke.Units.Select(unit => KeyInput(0, unit, KEYEVENTF_UNICODE | (isDown ? 0 : KEYEVENTF_KEYUP))).ToArray();
            }
            else
            {
                ushort scan = (ushort)MapVirtualKey(stroke.VirtualKey, 0);
                inputs = new[] { KeyInput(stroke.VirtualKey, scan, (stroke.Extended ? KEYEVENTF_EXTENDEDKEY : 0) | (isDown ? 0 : KEYEVENTF_KEYUP)) };
            }
            if (inputs.Length > 0 && SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) != inputs.Length)
                TrayLog.Write($"Uzaktan tuş uygulanamadı (SendInput hata {Marshal.GetLastWin32Error()}).");
        }

        private static INPUT KeyInput(ushort vk, ushort scan, uint flags) => new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags } },
        };

        // Kontrol oturumu bitti ya da servis bağlantısı koptu: panelden basılı kalan tuşlar bırakılır
        private void ReleasePressedKeys()
        {
            List<POps.Shared.RemoteKeyStroke> keys = _pressedKeys.TakeAll();
            foreach (POps.Shared.RemoteKeyStroke key in keys) SendKeyStroke(key, false);
            if (keys.Count > 0) TrayLog.Write($"Kontrol bitti; basılı kalan {keys.Count} tuş bırakıldı.");
        }


        // Ön plandaki uygulama. KVKK: yalnızca süreç adı gönderilir (ör. "chrome", "WINWORD"); pencere başlığı (açık
        // belge, site, sohbet adı) okunmaz.
        private async Task MonitorActiveAppAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    IntPtr hWnd = GetForegroundWindow();
                    if (hWnd != IntPtr.Zero && GetWindowThreadProcessId(hWnd, out uint pid) != 0 && pid != 0)
                    {
                        string app;
                        using (Process process = Process.GetProcessById((int)pid)) app = process.ProcessName;
                        if (!string.IsNullOrEmpty(app) && app != lastApp)
                        {
                            lastApp = app;
                            SendToService($"ACTIVE_APP:{app}");
                        }
                    }
                }
                catch { }
                await Task.Delay(2000, token);
            }
        }

        // Dönen: mesaj servise yazıldı mı (boru bağlı değilse ya da yazma başarısızsa false)
        private bool SendToService(string message)
        {
            if (pipeClient != null && pipeClient.IsConnected)
            {
                try
                {
                    byte[] data = Encoding.UTF8.GetBytes(message);
                    byte[] len = BitConverter.GetBytes(data.Length);
                    lock (_pipeLock)
                    {
                        pipeClient.Write(len, 0, 4);
                        pipeClient.Write(data, 0, data.Length);
                        pipeClient.Flush();
                    }
                    return true;
                }
                catch { }
            }
            return false;
        }

        private void SendToServiceBytes(byte[] data)
        {
            if (pipeClient != null && pipeClient.IsConnected)
            {
                try
                {
                    byte[] len = BitConverter.GetBytes(data.Length);
                    lock (_pipeLock)
                    {
                        pipeClient.Write(len, 0, 4);
                        pipeClient.Write(data, 0, data.Length);
                        pipeClient.Flush();
                    }
                }
                catch { }
            }
        }

        private void StartCaptureLoop(int fps)
        {
            if (_captureCts != null) return;
            _captureCts = new CancellationTokenSource();
            
            // Clamp fps to 1-5
            if (fps < 1) fps = 1;
            if (fps > 5) fps = 5;
            int delayMs = 1000 / fps;

            _ = Task.Run(async () =>
            {
                try
                {
                    while (!_captureCts.Token.IsCancellationRequested)
                    {
                        byte[] jpeg = CaptureScreenToJpeg();
                        if (jpeg != null) SendToServiceBytes(jpeg);
                        await Task.Delay(delayMs, _captureCts.Token);
                    }
                }
                catch (TaskCanceledException) { }
                catch { }
            }, _captureCts.Token);
        }

        private void StopCaptureLoop()
        {
            _captureCts?.Cancel();
            _captureCts = null;
        }

        // Yalnızca açık bir yakalama varsa hızını değiştirir; kendiliğinden yakalama başlatmaz
        private void ChangeCaptureFps(int fps)
        {
            if (_captureCts == null) return;
            StopCaptureLoop();
            StartCaptureLoop(fps);
        }

        private void SendSnapshot()
        {
            byte[] jpeg = CaptureScreenToJpeg();
            if (jpeg != null) SendToServiceBytes(jpeg);
        }

        // Her ekran önizlemesi görünür bir iz bırakır: simgenin ipucu metni son önizleme saatini gösterir,
        // balon bildirimi ise öğrenciyi rahatsız etmemek için en fazla 5 dakikada bir çıkar.
        private void NotifyPreviewTaken()
        {
            try
            {
                this.BeginInvoke(new Action(() =>
                {
                    trayIcon.Text = $"POps - Son ekran önizlemesi: {DateTime.Now:HH:mm}";
                    if (DateTime.Now - _lastPreviewBalloon >= PreviewBalloonInterval)
                    {
                        _lastPreviewBalloon = DateTime.Now;
                        ShowNotification("Gizlilik Bildirimi", "Bilgi İşlem ekranınızın küçük bir önizlemesini aldı.");
                    }
                }));
            }
            catch { }
        }

        private byte[]? CaptureScreenToJpeg()
        {
            try
            {
                Rectangle bounds = Screen.PrimaryScreen.Bounds;
                using Bitmap bitmap = new Bitmap(bounds.Width, bounds.Height);
                using (Graphics g = Graphics.FromImage(bitmap))
                {
                    g.CopyFromScreen(Point.Empty, Point.Empty, bounds.Size);
                }

                ImageCodecInfo jpegCodec = null;
                foreach (var codec in ImageCodecInfo.GetImageEncoders())
                {
                    if (codec.MimeType == "image/jpeg") { jpegCodec = codec; break; }
                }

                if (jpegCodec == null) return null;

                EncoderParameters ep = new EncoderParameters(1);
                ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 40L);

                using MemoryStream ms = new MemoryStream();
                bitmap.Save(ms, jpegCodec, ep);
                return ms.ToArray();
            }
            catch { return null; }
        }


        // ---------------------------------------------------------------- yardım masası
        private void OpenReportForm()
        {
            if (_reportForm == null || _reportForm.IsDisposed) _reportForm = new ReportProblemForm(m => SendToService(m));
            _reportForm.Show();
            _reportForm.Activate();
        }

        private void OpenTicketsForm()
        {
            if (_ticketsForm == null || _ticketsForm.IsDisposed) _ticketsForm = new MyTicketsForm(m => SendToService(m));
            else _ticketsForm.Request();
            _ticketsForm.Show();
            _ticketsForm.Activate();
        }

        private void OpenActivityForm()
        {
            if (_activityForm == null || _activityForm.IsDisposed) _activityForm = new ActivityForm(m => SendToService(m));
            else _activityForm.Request();
            _activityForm.Show();
            _activityForm.Activate();
        }

        // TICKET_RESULT / TICKET_LIST_RESULT / TICKET_NOTIFY / ACTIVITY_LIST_RESULT:<base64 JSON>
        private void HandleHelpdeskMessage(string message)
        {
            int colon = message.IndexOf(':');
            string kind = message.Substring(0, colon);
            using JsonDocument? doc = HelpdeskProtocol.Decode(message.Substring(colon + 1));
            if (doc == null) return;
            JsonElement root = doc.RootElement;
            this.Invoke(new Action(() =>
            {
                if (kind == "TICKET_RESULT")
                {
                    if (_reportForm != null && !_reportForm.IsDisposed) _reportForm.ShowResult(root);
                }
                else if (kind == "TICKET_LIST_RESULT")
                {
                    if (_ticketsForm != null && !_ticketsForm.IsDisposed) _ticketsForm.ShowTickets(root);
                }
                else if (kind == "ACTIVITY_LIST_RESULT")
                {
                    if (_activityForm != null && !_activityForm.IsDisposed) _activityForm.ShowActivity(root);
                }
                else
                {
                    long id = root.TryGetProperty("id", out var i) && i.TryGetInt64(out long n) ? n : 0;
                    ShowNotification("Talebinize yanıt geldi", $"#{id} {HelpdeskProtocol.Text(root, "subject")} ({HelpdeskProtocol.Text(root, "status_text")})\nGörmek için tıklayın.");
                    _lastBalloonIsTicket = true;
                    if (_ticketsForm != null && !_ticketsForm.IsDisposed && _ticketsForm.Visible) _ticketsForm.Request();
                }
            }));
        }

        private void OnAboutClicked(object? sender, EventArgs e)
        {
            MessageBox.Show("POps - POps Uç Nokta Ajanı\nYasal ve Etik Yönetim Sistemi", "Hakkında", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void OnAdminBypassClicked(object? sender, EventArgs e)
        {
            string token = ShowInputDialog("Yönetici Bypass", "POps Paneli üzerinden oluşturduğunuz 6 Haneli Bypass Token'ı girin:");
            if (string.IsNullOrWhiteSpace(token)) return;
            // Biçim hatası (ör. 0 yerine O) servise gitmez, deneme hakkı yemez
            if (!POps.Shared.BypassCode.IsWellFormed(token))
            {
                MessageBox.Show(KioskForm.CodeFormatHint, "Bilgi", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (!SendToService($"UNLOCK_BYPASS:{token}"))
            {
                MessageBox.Show(KioskForm.ServiceUnreachable, "Bilgi", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            MessageBox.Show("Bypass Token gönderildi. Token doğruysa kilit ekranı ve ağ izolasyonu kaldırılacaktır.", "Bilgi", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private string ShowInputDialog(string title, string promptText)
        {
            Form form = new Form();
            Label label = new Label();
            TextBox textBox = new TextBox();
            Button buttonOk = new Button();
            Button buttonCancel = new Button();

            form.Text = title;
            label.Text = promptText;
            textBox.Text = "";

            buttonOk.Text = "Onayla";
            buttonCancel.Text = "İptal";
            buttonOk.DialogResult = DialogResult.OK;
            buttonCancel.DialogResult = DialogResult.Cancel;

            label.SetBounds(9, 20, 372, 13);
            textBox.SetBounds(12, 36, 372, 20);
            buttonOk.SetBounds(228, 72, 75, 23);
            buttonCancel.SetBounds(309, 72, 75, 23);

            label.AutoSize = true;
            textBox.Anchor = textBox.Anchor | AnchorStyles.Right;
            buttonOk.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            buttonCancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;

            form.ClientSize = new Size(396, 107);
            form.Controls.AddRange(new Control[] { label, textBox, buttonOk, buttonCancel });
            form.ClientSize = new Size(Math.Max(300, label.Right + 10), form.ClientSize.Height);
            form.FormBorderStyle = FormBorderStyle.FixedDialog;
            form.StartPosition = FormStartPosition.CenterScreen;
            form.MinimizeBox = false;
            form.MaximizeBox = false;
            form.AcceptButton = buttonOk;
            form.CancelButton = buttonCancel;

            DialogResult dialogResult = form.ShowDialog();
            return dialogResult == DialogResult.OK ? textBox.Text : "";
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                cts.Cancel();
                pipeClient?.Dispose();
                trayIcon?.Dispose();
                trayMenu?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
