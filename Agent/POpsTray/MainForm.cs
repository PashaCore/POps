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

namespace POpsTray
{
    public partial class MainForm : Form
    {
        private NotifyIcon trayIcon;
        private ContextMenuStrip trayMenu;
        private NamedPipeClientStream? pipeClient;
        private CancellationTokenSource cts = new CancellationTokenSource();
        private string lastWindow = "";
        private bool isStealthMode = false;
        private KioskForm _activeKioskForm;
        private CancellationTokenSource? _captureCts;
        private readonly object _pipeLock = new object();

        // UIPI gerektirmeyen, doğrudan User Session'da çalışan API'ler
        [DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

        [DllImport("user32.dll")]
        public static extern bool LockWorkStation();

        [DllImport("user32.dll")]
        static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, int dwExtraInfo);

        [DllImport("user32.dll")]
        static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, int dwExtraInfo);

        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;
        private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
        private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
        private const uint MOUSEEVENTF_WHEEL = 0x0800;

        private const uint KEYEVENTF_KEYDOWN = 0x0000;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        public MainForm(bool isStealth = false)
        {
            this.isStealthMode = isStealth;
            this.ShowInTaskbar = false;
            this.WindowState = FormWindowState.Minimized;
            this.FormBorderStyle = FormBorderStyle.FixedToolWindow;
            this.Opacity = 0;

            trayMenu = new ContextMenuStrip();
            trayMenu.Items.Add("Ekran İzlemeyi Duraklat (15 Dk)", null, OnPauseClicked);
            trayMenu.Items.Add("Koruyucuyu (WatchDog) Duraklat", null, OnWatchDogPauseClicked);
            trayMenu.Items.Add(new ToolStripMenuItem("Hakkında", null, OnAboutClicked));
            trayMenu.Items.Add(new ToolStripMenuItem("Çıkış", null, OnExitClicked));
            trayMenu.Items.Add("-");
            var adminItem = new ToolStripMenuItem("Yönetici Müdahalesi (Bypass)", null, OnAdminBypassClicked);
            adminItem.ForeColor = Color.Red;
            trayMenu.Items.Add(adminItem);

            trayIcon = new NotifyIcon();
            trayIcon.Text = "POps - Ajanı";
            trayIcon.Icon = SystemIcons.Shield; // İleride özel bir .ico dosyası yüklenebilir
            trayIcon.ContextMenuStrip = trayMenu;
            
            if (!isStealthMode)
            {
                trayIcon.Visible = true;
            }

            _ = Task.Run(() => ConnectToServiceAsync(cts.Token));
            _ = Task.Run(() => MonitorActiveWindowAsync(cts.Token));
        }

        public void ShowNotification(string title, string message)
        {
            if (isStealthMode) return; // Hayalet modundaysa bildirim gösterme
            
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
                }
            }
        }

        private void ProcessMessageFromService(string jsonMsg)
        {
            try
            {
                File.AppendAllText(@"C:\POpsLogs\TrayLog.txt", $"[TRAY] Received: {jsonMsg}\n");

                if (jsonMsg.StartsWith("START_CAPTURE")) 
                { 
                    int fps = 2;
                    if (jsonMsg.Contains(":")) int.TryParse(jsonMsg.Split(':')[1], out fps);
                    StartCaptureLoop(fps); 
                    return; 
                }
                if (jsonMsg.Contains("STOP_CAPTURE")) { StopCaptureLoop(); return; }
                if (jsonMsg.Contains("CAPTURE_SNAPSHOT")) { SendSnapshot(); return; }
                
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
                        this.Invoke(new Action(() => 
                        {
                            if (_activeKioskForm == null || _activeKioskForm.IsDisposed)
                            {
                                _activeKioskForm = new KioskForm(reason);
                                _activeKioskForm.Show();
                            }
                            ShowNotification("Acil Durum İzolasyonu", $"Cihaz BT tarafından kilitlendi!\nNeden: {reason}");
                        }));
                    }
                    else if (action == "unlock")
                    {
                        this.Invoke(new Action(() => 
                        {
                            if (_activeKioskForm != null && !_activeKioskForm.IsDisposed)
                            {
                                _activeKioskForm.AllowClose = true;
                                _activeKioskForm.Hide();
                                _activeKioskForm.Close();
                                _activeKioskForm = null;
                            }
                            ShowNotification("Karantina Kaldırıldı", "Cihazın karantina durumu sistem yöneticisi tarafından kaldırıldı.");
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
                        this.Invoke(new Action(() => 
                        {
                            ShowNotification("Gizlilik Bildirimi", "Sistem yöneticisi ekran önizlemenizi güncelledi.");
                        }));
                        SendSnapshot();
                    }
                    else if (action == "stop_stream")
                    {
                        StopCaptureLoop();
                    }
                }
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\POpsLogs\TrayLog.txt", $"[TRAY] Error: {ex.Message}\n{ex.StackTrace}\n");
            }
        }

        private void ProcessRemoteInput(JsonElement root)
        {
            try
            {
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
                    string keyStr = root.GetProperty("key").GetString();
                    bool isDown = root.GetProperty("is_down").GetBoolean();
                    uint flag = isDown ? KEYEVENTF_KEYDOWN : KEYEVENTF_KEYUP;
                    
                    byte vk = MapJsKeyToVK(keyStr);
                    if (vk != 0)
                    {
                        keybd_event(vk, 0, flag, 0);
                    }
                }
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\POpsLogs\TrayLog.txt", $"[TRAY] Remote Input Error: {ex.Message}\n");
            }
        }

        private byte MapJsKeyToVK(string jsKey)
        {
            if (jsKey.Length == 1)
            {
                char c = char.ToUpperInvariant(jsKey[0]);
                return (byte)c;
            }
            
            return jsKey switch
            {
                "Enter" => 0x0D,
                "Backspace" => 0x08,
                "Tab" => 0x09,
                "Escape" => 0x1B,
                "Space" => 0x20,
                "ArrowLeft" => 0x25,
                "ArrowUp" => 0x26,
                "ArrowRight" => 0x27,
                "ArrowDown" => 0x28,
                "Delete" => 0x2E,
                "Shift" => 0x10,
                "Control" => 0x11,
                "Alt" => 0x12,
                "Meta" => 0x5B, 
                _ => 0
            };
        }


        private async Task MonitorActiveWindowAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    IntPtr hWnd = GetForegroundWindow();
                    if (hWnd != IntPtr.Zero)
                    {
                        StringBuilder sb = new StringBuilder(256);
                        GetWindowText(hWnd, sb, 256);
                        string currentWindow = sb.ToString();

                        if (!string.IsNullOrEmpty(currentWindow) && currentWindow != lastWindow)
                        {
                            lastWindow = currentWindow;
                            SendToService($"ACTIVE_WINDOW:{currentWindow}");
                        }
                    }
                }
                catch { }
                await Task.Delay(2000, token);
            }
        }

        private void SendToService(string message)
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
                }
                catch { }
            }
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

        private void SendSnapshot()
        {
            byte[] jpeg = CaptureScreenToJpeg();
            if (jpeg != null) SendToServiceBytes(jpeg);
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


        private void OnPauseClicked(object? sender, EventArgs e)
        {
            ShowNotification("Bağlantı Duraklatıldı", "Ekran izleme 15 dakika boyunca askıya alındı.");
            SendToService("USER_COMMAND:PAUSE_VISION");
        }

        private void OnWatchDogPauseClicked(object? sender, EventArgs e)
        {
            ShowNotification("WatchDog Uyutuldu", "Koruyucu servis duraklatıldı.");
            SendToService("USER_COMMAND:PAUSE_WATCHDOG");
        }

        private void OnAboutClicked(object? sender, EventArgs e)
        {
            MessageBox.Show("POps - POps Uç Nokta Ajanı\nYasal ve Etik Yönetim Sistemi", "Hakkında", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void OnExitClicked(object? sender, EventArgs e)
        {
            Application.Exit();
        }

        private void OnAdminBypassClicked(object? sender, EventArgs e)
        {
            string token = ShowInputDialog("Yönetici Bypass", "POps Paneli üzerinden oluşturduğunuz 6 Haneli Bypass Token'ı girin:");
            if (!string.IsNullOrEmpty(token))
            {
                SendToService($"UNLOCK_BYPASS:{token}");
                MessageBox.Show("Bypass Token gönderildi. Eğer token doğruysa ağ izolasyonu kaldırılacaktır.", "Bilgi", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
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
