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
        // Vision v2 (sunucu ikili kareyi destekliyorsa): DXGI/GDI, bölgeler, ekran seçimi, uyarlanır kalite
        private readonly POpsTray.Vision.VisionStreamer _visionV2;
        // Pano paylaşımı yalnızca kullanıcının kabul ettiği v2 oturumunda (servis CLIPBOARD_SHARE:1 ile açar)
        private bool _clipboardShare;
        private string? _clipboardFromAdmin;
        private DateTime _lastClipboardNotice = DateTime.MinValue;
        private const int WM_CLIPBOARDUPDATE = 0x031D;
        // Bilgisayar kilitliyken başlayan oturumun bildirimi (docs/vision.md, karar 5): servis kurar (VISION_NOTICE_ON),
        // kullanıcının masaüstü geri gelince şerit ve balon gösterilir, oturum bitene kadar kalır. O zamana kadar
        // yakalama bekletilir (bkz. POps.Shared.VisionSessionNotice).
        private readonly POps.Shared.VisionSessionNotice _sessionNotice = new POps.Shared.VisionSessionNotice();
        private POps.Shared.VisionNoticeInfo? _sessionNoticeInfo;
        private POpsTray.Vision.SessionBanner? _sessionBanner;
        private System.Windows.Forms.Timer? _sessionNoticeTimer;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool AddClipboardFormatListener(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
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

            _visionV2 = new POpsTray.Vision.VisionStreamer(data => SendToServiceBytes(data), text => SendToService(text), CapturableDesktop);
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

            _ = Task.Run(() => ConnectToServiceAsync(cts.Token));
            _ = Task.Run(() => MonitorActiveAppAsync(cts.Token));
        }

        private bool _configErrorShown;

        // Simge uyarıya döner; bildirim tepsi oturumu başına bir kez
        private void ShowConfigError(string detail)
        {
            trayIcon.Icon = SystemIcons.Warning;
            trayIcon.Text = "POps - yapılandırma okunamadı";
            if (_configErrorShown) return;
            _configErrorShown = true;
            if (detail.Length > 150) detail = string.Concat(detail.AsSpan(0, 150), "…");
            ShowNotification("POps: yapılandırma okunamadı",
                "Ajan sunucuya bağlanamıyor, bu bilgisayar yönetilmiyor. BT ekibine bildirin." + (detail.Length > 0 ? $" ({detail})" : ""));
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
                            int r = await pipeClient.ReadAsync(lBuf.AsMemory(lRead, 4 - lRead), token);
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
                            int r = await pipeClient.ReadAsync(d.AsMemory(total, dLen - total), token);
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
                    // Onaylı oturum da bitti (servis tepsi kopunca uzaktan girdiyi keser): yakalama durur, oturum bildirimi
                    // kapanır. Yeniden bağlanınca eski oturum bildirimsiz sürmesin.
                    if (_captureCts != null || _visionV2.Running || _sessionNotice.Armed) EndCapture();
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

                // Kilitliyken başlayan oturumun bildirimi (yakalama başlamadan önce gelir)
                if (HandleSessionNotice(jsonMsg)) return;
                // Vision v2 (eski START_CAPTURE'dan önce: o da "START_CAPTURE" ile başlar)
                if (HandleVisionV2(jsonMsg)) return;

                if (jsonMsg.StartsWith("START_CAPTURE")) 
                { 
                    int fps = 2;
                    if (jsonMsg.Contains(":")) int.TryParse(jsonMsg.Split(':')[1], out fps);
                    StartCaptureLoop(fps); 
                    return; 
                }
                if (jsonMsg.Contains("STOP_CAPTURE"))
                {
                    EndCapture();
                    return;
                }
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
                
                // Servis yapılandırmasını okuyamadı (sunucu adresi yok ya da geçersiz): sağlıklı görünmesin
                if (jsonMsg.StartsWith("CONFIG_ERROR:"))
                {
                    string detail = "";
                    try { detail = Encoding.UTF8.GetString(Convert.FromBase64String(jsonMsg.Substring("CONFIG_ERROR:".Length))); }
                    catch (FormatException) { }
                    this.Invoke(new Action(() => ShowConfigError(detail)));
                    return;
                }

                // Sınav modu: bant, bildirim ve engellenen uygulama
                if (HandleExamMessage(jsonMsg)) return;

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
                        // Onay isteği her zaman kullanıcıya sorulur; bilgisayar kilitliyken gelirse pencere kullanıcının
                        // masaüstünde yanıt bekler, oturum yalnızca "Evet" ile başlar (sessiz başlama yok)
                        POps.Shared.VisionStartPlan plan = POps.Shared.VisionSessionStart.Plan(isMandatory, countdownSec);
                        
                        this.Invoke(new Action(() => 
                        {
                            if (plan == POps.Shared.VisionStartPlan.Countdown)
                            {
                                CountdownForm cf = new CountdownForm(countdownSec, reason, isQuarantined, () =>
                                {
                                    ShowNotification("Kurumsal Bildirim", $"Bilgi İşlem yetkilisi {adminName} cihazınıza bağlandı.\nİşlem No: {sessionId}");
                                    StartVisionTunnel(fps);
                                });
                                cf.Show();
                            }
                            else if (plan == POps.Shared.VisionStartPlan.Immediate)
                            {
                                ShowNotification("Kurumsal Bildirim", $"Bilgi İşlem yetkilisi {adminName} bakım/güvenlik amacıyla bu cihaza uzaktan bağlanacaktır.\nİşlem No: {sessionId}\nBu işlem kayıt altına alınacaktır.");
                                StartVisionTunnel(fps);
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
                                    StartVisionTunnel(fps);
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
                    if (_visionV2.Running) MoveCursorV2(x, y);
                    else SetCursorPos(x, y);
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
            CancellationToken token = _captureCts.Token;

            _ = Task.Run(async () =>
            {
                var desktop = new POps.Shared.VisionDesktopGate();
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        byte[]? jpeg = NextLegacyFrame(desktop);
                        if (jpeg != null) SendToServiceBytes(jpeg);
                        await Task.Delay(delayMs, token);
                    }
                }
                catch (TaskCanceledException) { }
                catch { }
            }, token);
        }

        // Eski yayın (JSON stream_frame): güvenli masaüstü (UAC onayı, kilit, oturum açma ekranı) etkinken GDI yakalaması
        // başarısız olur ve eskiden hiç kare gitmiyordu (panelde donmuş son görüntü). Artık o sırada bildirim resmi gider:
        // ilk seferde ve 5 sn'de bir. Masaüstü geri gelince yakalama sürer.
        private byte[]? NextLegacyFrame(POps.Shared.VisionDesktopGate desktop)
        {
            POps.Shared.VisionDesktopStep step = desktop.Next(CapturableDesktop(), needFull: false, DateTime.UtcNow);
            if (step == POps.Shared.VisionDesktopStep.Enter)
                TrayLog.Write("Vision: kullanıcının masaüstü görünmüyor (güvenli masaüstü); görüntü yerine bildirim gönderiliyor.");
            else if (step == POps.Shared.VisionDesktopStep.Resume)
                TrayLog.Write("Vision: kullanıcının masaüstü geri geldi; yakalama sürüyor.");

            if (step is POps.Shared.VisionDesktopStep.Enter or POps.Shared.VisionDesktopStep.Notice)
            {
                Rectangle bounds = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1280, 720);
                return POpsTray.Vision.SecureDesktopNotice.Jpeg(bounds.Width, bounds.Height, 40L);
            }
            if (step == POps.Shared.VisionDesktopStep.Wait) return null;
            return CaptureScreenToJpeg();
        }

        private void StopCaptureLoop()
        {
            _captureCts?.Cancel();
            _captureCts = null;
        }

        // Oturum bitti (STOP_CAPTURE ya da servis bağlantısı koptu): yakalama, pano paylaşımı, uzak tuşlar ve oturum bildirimi
        private void EndCapture()
        {
            StopCaptureLoop();
            _visionV2.Stop();
            SetClipboardShare(false);
            ReleasePressedKeys();
            EndSessionNotice();
        }

        // Yakalama döngüleri (eski JPEG ve v2) her kareden önce sorar: kullanıcının masaüstü ekranda mı ve kilitliyken
        // başlayan oturumun bildirimi gösterildi mi. Bildirim bekliyorsa masaüstü görünmüyor sayılır (bildirim resmi gider).
        private bool CapturableDesktop() => _sessionNotice.Capturable(POpsTray.Vision.InputDesktop.IsOwn());

        // ---------------------------------------------------------------- kilitliyken başlayan oturum
        // Oturumu başlat. Kullanıcının masaüstü o an ekranda değilse (kilit, oturum açma, UAC ya da Ctrl+Alt+Del ekranı)
        // kullanıcı geri sayımı görmedi: servis 1150 yazar ve oturum bildirimini kurdurur (VISION_NOTICE_ON).
        private void StartVisionTunnel(int fps)
        {
            bool locked = !POpsTray.Vision.InputDesktop.IsOwn();
            if (locked) TrayLog.Write("Vision: oturum kullanıcının masaüstü görünmüyorken başladı (kilit ya da güvenli masaüstü); bildirim masaüstü geri gelince gösterilecek.");
            SendToService(POps.Shared.VisionSessionStart.TunnelMessage(fps, locked));
        }

        // VISION_NOTICE_ON:<base64 JSON>, VISION_NOTICE_OFF. Dönen: mesaj bildirime aitti.
        private bool HandleSessionNotice(string message)
        {
            POps.Shared.VisionNoticeInfo? info = POps.Shared.VisionSessionNotice.ParseOn(message);
            if (info != null)
            {
                try { this.Invoke(new Action(() => ArmSessionNotice(info))); }
                catch (InvalidOperationException) { }
                return true;
            }
            if (message == POps.Shared.VisionSessionNotice.Off)
            {
                EndSessionNotice();
                return true;
            }
            return false;
        }

        // UI iş parçacığında. Masaüstü zaten görünüyorsa hemen, değilse geri gelince (yarım saniyede bir bakılır) gösterilir.
        private void ArmSessionNotice(POps.Shared.VisionNoticeInfo info)
        {
            _sessionNoticeInfo = info;
            _sessionNotice.Arm();
            if (_sessionBanner != null)
            {
                _sessionBanner.ShowMessage(SessionBannerText(info));
                return;
            }
            if (_sessionNoticeTimer == null)
            {
                _sessionNoticeTimer = new System.Windows.Forms.Timer { Interval = 500 };
                _sessionNoticeTimer.Tick += (_, _) => CheckSessionNotice();
            }
            CheckSessionNotice();
            if (_sessionNotice.Waiting) _sessionNoticeTimer.Start();
        }

        // UI iş parçacığında: masaüstü geri geldiyse şerit ekrana konur, sonra yakalama serbest kalır (önce bildirim, sonra
        // görüntü)
        private void CheckSessionNotice()
        {
            if (!_sessionNotice.Due(POpsTray.Vision.InputDesktop.IsOwn())) return;
            _sessionNoticeTimer?.Stop();
            POps.Shared.VisionNoticeInfo info = _sessionNoticeInfo ?? new POps.Shared.VisionNoticeInfo(null, null, null, false);
            _sessionBanner ??= new POpsTray.Vision.SessionBanner();
            _sessionBanner.ShowMessage(SessionBannerText(info));
            _sessionNotice.MarkShown();
            string admin = info.RequestedBy == null ? "" : $" yetkilisi {POps.Shared.LogText.Safe(info.RequestedBy, 80)}";
            string id = info.SessionId == null ? "" : $"\nİşlem No: {POps.Shared.LogText.Safe(info.SessionId, 40)}";
            ShowNotification("Kurumsal Bildirim", $"Bilgisayarınız kilitliyken Bilgi İşlem{admin} ekranınıza bağlandı. Oturum sürdükçe ekranın altında bildirim görünür.{id}");
            TrayLog.Write("Vision: kullanıcının masaüstü geri geldi; kilitliyken başlayan oturumun bildirimi gösterildi.");
        }

        private static string SessionBannerText(POps.Shared.VisionNoticeInfo info)
        {
            var details = new List<string>();
            if (info.RequestedBy != null) details.Add("Yetkili: " + POps.Shared.LogText.Safe(info.RequestedBy, 80));
            if (!string.IsNullOrWhiteSpace(info.Reason)) details.Add("Gerekçe: " + POps.Shared.LogText.Safe(info.Reason, 120));
            if (info.SessionId != null) details.Add("İşlem No: " + POps.Shared.LogText.Safe(info.SessionId, 40));
            string first = "Ekranınız Bilgi İşlem tarafından izleniyor (oturum bilgisayarınız kilitliyken başladı)";
            return details.Count == 0 ? first : first + "\n" + string.Join("  ·  ", details);
        }

        // Oturum bitti: şerit kapanır, gösterilmişse kullanıcıya söylenir
        private void EndSessionNotice()
        {
            if (!_sessionNotice.Armed && _sessionBanner == null) return;
            try
            {
                this.Invoke(new Action(() =>
                {
                    bool shown = _sessionNotice.End();
                    _sessionNoticeTimer?.Stop();
                    _sessionNoticeInfo = null;
                    if (_sessionBanner != null)
                    {
                        _sessionBanner.AllowClose = true;
                        _sessionBanner.Close();
                        _sessionBanner.Dispose();
                        _sessionBanner = null;
                    }
                    if (shown) ShowNotification("Kurumsal Bildirim", "Bilgi İşlem oturumu sona erdi.");
                }));
            }
            catch (InvalidOperationException) { }
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

        // ---------------------------------------------------------------- Vision v2
        // Servis mesajları: START_CAPTURE_V2:fps, VISION_SELECT:n|all, VISION_QUALITY:q,s,fps, VISION_DROPPED,
        // CLIPBOARD_SHARE:0|1, CLIPBOARD_SET:<base64>. Dönen: mesaj v2'ye aitti.
        private bool HandleVisionV2(string message)
        {
            if (message.StartsWith("START_CAPTURE_V2:", StringComparison.Ordinal))
            {
                StopCaptureLoop();
                _visionV2.Start(int.TryParse(message.AsSpan("START_CAPTURE_V2:".Length), out int fps) ? fps : POps.Shared.VisionQuality.DefaultFps);
                return true;
            }
            if (message.StartsWith("VISION_SELECT:", StringComparison.Ordinal))
            {
                if (POps.Shared.VisionFrame.TryParseMonitor(message.Substring("VISION_SELECT:".Length), out byte monitor)) _visionV2.Select(monitor);
                return true;
            }
            if (message.StartsWith("VISION_QUALITY:", StringComparison.Ordinal))
            {
                if (POps.Shared.VisionQuality.TryParse(message.Substring("VISION_QUALITY:".Length), out int quality, out double scale, out int fps))
                    _visionV2.SetQuality(quality, scale, fps);
                return true;
            }
            if (message == "VISION_DROPPED")
            {
                _visionV2.OnDropped();
                return true;
            }
            if (message.StartsWith("CLIPBOARD_SHARE:", StringComparison.Ordinal))
            {
                SetClipboardShare(message.EndsWith(":1", StringComparison.Ordinal));
                return true;
            }
            if (message.StartsWith("CLIPBOARD_SET:", StringComparison.Ordinal))
            {
                string text;
                try { text = Encoding.UTF8.GetString(Convert.FromBase64String(message.Substring("CLIPBOARD_SET:".Length))); }
                catch (FormatException) { return true; }
                this.Invoke(new Action(() =>
                {
                    if (!_clipboardShare) return;
                    try
                    {
                        _clipboardFromAdmin = text;
                        Clipboard.SetText(text);
                        NotifyClipboard("Yönetici panonuza metin koydu.");
                    }
                    catch (ExternalException) { }
                }));
                return true;
            }
            return false;
        }

        // ---------------------------------------------------------------- sınav modu
        private ExamBanner? _examBanner;
        private DateTime _lastExamAppNotice = DateTime.MinValue;

        // EXAM_ON:<base64 {"message","until"}>, EXAM_OFF, EXAM_APP_BLOCKED:<ad.exe>. Dönen: mesaj sınav moduna aitti.
        private bool HandleExamMessage(string message)
        {
            if (message.StartsWith("EXAM_ON:", StringComparison.Ordinal))
            {
                string text = "Sınav modu";
                long? until = null;
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(Convert.FromBase64String(message.Substring("EXAM_ON:".Length)));
                    if (doc.RootElement.TryGetProperty("message", out JsonElement m) && m.ValueKind == JsonValueKind.String) text = m.GetString() ?? text;
                    if (doc.RootElement.TryGetProperty("until", out JsonElement u) && u.ValueKind == JsonValueKind.Number) until = u.GetInt64();
                }
                catch (Exception ex) when (ex is FormatException || ex is JsonException) { }
                this.Invoke(new Action(() =>
                {
                    bool first = _examBanner == null;
                    _examBanner ??= new ExamBanner();
                    _examBanner.ShowMessage(text, until);
                    if (first) ShowNotification("Sınav modu", text);
                }));
                return true;
            }
            if (message == "EXAM_OFF")
            {
                this.Invoke(new Action(() =>
                {
                    if (_examBanner == null) return;
                    _examBanner.AllowClose = true;
                    _examBanner.Close();
                    _examBanner.Dispose();
                    _examBanner = null;
                    ShowNotification("Sınav modu bitti", "İnternet erişimi normale döndü.");
                }));
                return true;
            }
            // Dosya aktarımı: kullanıcıya hep söylenir
            if (message.StartsWith("FILE_PUSHED:", StringComparison.Ordinal))
            {
                string name = message.Substring("FILE_PUSHED:".Length);
                this.Invoke(new Action(() => ShowNotification("Dosya", $"Yönetici bir dosya gönderdi: {name}")));
                return true;
            }
            if (message.StartsWith("FILE_PULLED:", StringComparison.Ordinal))
            {
                string path = message.Substring("FILE_PULLED:".Length);
                this.Invoke(new Action(() => ShowNotification("Dosya", $"Yönetici bu dosyayı aldı: {path}")));
                return true;
            }
            if (message.StartsWith("EXAM_APP_BLOCKED:", StringComparison.Ordinal))
            {
                string app = message.Substring("EXAM_APP_BLOCKED:".Length);
                this.Invoke(new Action(() =>
                {
                    if (DateTime.Now - _lastExamAppNotice < TimeSpan.FromSeconds(10)) return;
                    _lastExamAppNotice = DateTime.Now;
                    ShowNotification("Sınav modu", $"{app} sınav sırasında kullanılamaz.");
                }));
                return true;
            }
            return false;
        }

        // Görüntüleyicinin koordinatı seçili ekranın fiziksel pikselidir; imleç per-monitor DPI bağlamında konur
        private void MoveCursorV2(int x, int y)
        {
            IntPtr previous = POpsTray.Vision.Dpi.EnterPerMonitor();
            try
            {
                if (_visionV2.TryMapToScreen(x, y, out Point screen)) SetCursorPos(screen.X, screen.Y);
            }
            finally { POpsTray.Vision.Dpi.Restore(previous); }
        }

        private void SetClipboardShare(bool enabled)
        {
            try
            {
                this.Invoke(new Action(() =>
                {
                    if (enabled == _clipboardShare) return;
                    _clipboardShare = enabled;
                    if (enabled) AddClipboardFormatListener(Handle);
                    else RemoveClipboardFormatListener(Handle);
                }));
            }
            catch (InvalidOperationException) { }
        }

        // Kullanıcının kopyaladığı metin (yalnızca paylaşım açıkken, en çok 64 KB) yöneticiye gider
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_CLIPBOARDUPDATE && _clipboardShare)
            {
                try
                {
                    if (Clipboard.ContainsText())
                    {
                        string text = Clipboard.GetText();
                        bool echo = text == _clipboardFromAdmin;
                        _clipboardFromAdmin = null;
                        byte[] bytes = Encoding.UTF8.GetBytes(text);
                        if (!echo && bytes.Length > 0 && bytes.Length <= 64 * 1024 && SendToService("CLIPBOARD:" + Convert.ToBase64String(bytes)))
                            NotifyClipboard("Kopyaladığınız metin yöneticiye gönderildi.");
                    }
                }
                catch (ExternalException) { }
            }
            base.WndProc(ref m);
        }

        // Kullanıcıya bildirim: "Pano paylaşıldı" (en çok 10 sn'de bir balon)
        private void NotifyClipboard(string detail)
        {
            if (DateTime.Now - _lastClipboardNotice < TimeSpan.FromSeconds(10)) return;
            _lastClipboardNotice = DateTime.Now;
            ShowNotification("Pano paylaşıldı", detail);
        }

        private void OnDisplaySettingsChanged(object? sender, EventArgs e) => _visionV2.OnDisplaySettingsChanged();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
                _visionV2.Dispose();
                _examBanner?.Dispose();
                _sessionBanner?.Dispose();
                _sessionNoticeTimer?.Dispose();
                cts.Cancel();
                pipeClient?.Dispose();
                trayIcon?.Dispose();
                trayMenu?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
