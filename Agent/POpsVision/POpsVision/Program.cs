using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace POpsVision
{
    class Program
    {
        public const string APP_VERSION = "1.0.7-FULL-COMMAND";

        // --- WIN32 API ENTEGRASYONLARI ---
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
        [DllImport("shcore.dll")] static extern int SetProcessDpiAwareness(int awareness);
        [DllImport("kernel32.dll")] static extern IntPtr GetConsoleWindow();
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        const int SW_HIDE = 0;
        const int SW_SHOW = 5;

        // UIPI (İzolasyon) Aşma API'leri
        [DllImport("user32.dll")] static extern bool ChangeWindowMessageFilter(uint msg, uint dwFlag);
        const uint MSGFLT_ADD = 1;
        const uint WM_DROPFILES = 0x0233;
        const uint WM_COPYDATA = 0x004A;
        const uint WM_COPYGLOBALDATA = 0x0049;

        [DllImport("user32.dll")] static extern int GetSystemMetrics(int nIndex);
        [DllImport("user32.dll")] static extern bool SetCursorPos(int X, int Y);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
        [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT lpPoint);

        // 🚀 KLAVYE İÇİN AKILLI DİL ALGILAYICI
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern short VkKeyScan(char ch);

        [DllImport("user32.dll", SetLastError = true)]
        static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        struct INPUT { public int type; public InputUnion u; }

        [StructLayout(LayoutKind.Explicit)]
        struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; [FieldOffset(0)] public HARDWAREINPUT hi; }

        [StructLayout(LayoutKind.Sequential)]
        struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

        [StructLayout(LayoutKind.Sequential)]
        struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

        [StructLayout(LayoutKind.Sequential)]
        struct HARDWAREINPUT { public uint uMsg; public ushort wParamL; public ushort wParamH; }

        const int INPUT_MOUSE = 0;
        const int INPUT_KEYBOARD = 1;
        const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        const uint MOUSEEVENTF_LEFTUP = 0x0004;
        const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
        const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
        const uint MOUSEEVENTF_WHEEL = 0x0800;
        const uint KEYEVENTF_KEYDOWN = 0x0000;
        const uint KEYEVENTF_KEYUP = 0x0002;
        const uint KEYEVENTF_EXTENDEDKEY = 0x0001;

        // Motor Ayarları
        static string _hwId = "";
        static NamedPipeClientStream _pipeClient;
        static bool _isCapturing = false;
        static int _targetFps = 30; // Varsayılan 30, panelden değiştirilebilir.
        static long _jpegQuality = 60L;
        static double _scaleFactor = 0.75;

        static ImageCodecInfo _jpegEncoder;
        static EncoderParameters _encoderParameters;
        static readonly SemaphoreSlim _pipeWriteLock = new SemaphoreSlim(1, 1);

        static ScreenEngine _screenEngine = new ScreenEngine();

        static async Task Main(string[] args)
        {
            try
            {
                IntPtr hWnd = GetConsoleWindow();
                if (hWnd != IntPtr.Zero) ShowWindow(hWnd, SW_HIDE); // Canlı sistemde gizli kalmalı

                try
                {
                    ChangeWindowMessageFilter(WM_DROPFILES, MSGFLT_ADD);
                    ChangeWindowMessageFilter(WM_COPYDATA, MSGFLT_ADD);
                    ChangeWindowMessageFilter(WM_COPYGLOBALDATA, MSGFLT_ADD);
                }
                catch { }

                if (args != null && args.Length > 0 && args.Contains("POpsV", StringComparer.OrdinalIgnoreCase))
                {
                    SpawnVersionWindow();
                    return;
                }

                POpsHelpers.Log("VISION", $"POps Vision Engine Başlatıldı (v{APP_VERSION})");

                SetDpiAwareness();

                _jpegEncoder = GetEncoder(ImageFormat.Jpeg);
                _encoderParameters = new EncoderParameters(1);
                _encoderParameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, _jpegQuality);

                _hwId = POpsHelpers.GetHardwareId();

                while (string.IsNullOrEmpty(_hwId) || _hwId == "HW-UNKNOWN")
                {
                    await Task.Delay(5000);
                    _hwId = POpsHelpers.GetHardwareId();
                }

                string pipeName = $@"Global\POpsVisionPipe_{_hwId}";

                if (!_screenEngine.Start()) POpsHelpers.Log("VISION", "Ekran yakalama (DXGI) başlatılamadı!", true);

                while (true)
                {
                    try
                    {
                        _pipeClient?.Dispose();
                        _pipeClient = null;
                        _isCapturing = false;

                        _pipeClient = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

                        using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                        await _pipeClient.ConnectAsync(connectCts.Token);

                        POpsHelpers.Log("VISION", "🟢 BAŞARILI: Ajan ile Tünel Kuruldu!");

                        var readTask = ListenCommandsAsync();
                        var captureTask = CaptureAndStreamAsync();

                        await Task.WhenAny(readTask, captureTask);
                    }
                    catch (OperationCanceledException)
                    {
                        _isCapturing = false;
                        await Task.Delay(5000);
                    }
                    catch (Exception ex)
                    {
                        POpsHelpers.Log("VISION", $"Bağlantı Koptu: {ex.Message}", true);
                        _isCapturing = false;
                        await Task.Delay(3000);
                    }
                }
            }
            catch (Exception fatalEx)
            {
                POpsHelpers.Log("VISION", $"[ÖLÜMCÜL HATA] Vision patladı: {fatalEx.Message}", true);
            }
        }

        static void SpawnVersionWindow()
        {
            try
            {
                string cmd = $"$Host.UI.RawUI.WindowTitle = 'POps Vision Engine'; " +
                             $"Write-Host '========================================' -ForegroundColor Cyan; " +
                             $"Write-Host ' POps Vision Engine - Versiyon: {APP_VERSION}' -ForegroundColor Green; " +
                             $"Write-Host '========================================' -ForegroundColor Cyan; " +
                             $"Read-Host 'Kapatmak için Enter tuşuna basın'";

                ProcessStartInfo psi = new ProcessStartInfo { FileName = "powershell.exe", Arguments = $"-NoProfile -Command \"{cmd}\"", UseShellExecute = true, CreateNoWindow = false };
                Process.Start(psi);
            }
            catch { }
        }

        static void SetDpiAwareness()
        {
            try
            {
                // Fare sapmasını önlemek için en agresif DPI modu
                if (Environment.OSVersion.Version.Major >= 10) SetProcessDpiAwareness(2);
                else SetProcessDPIAware();
            }
            catch { }
        }

        static async Task ListenCommandsAsync()
        {
            byte[] lengthBuffer = new byte[4];
            try
            {
                while (_pipeClient != null && _pipeClient.IsConnected)
                {
                    int bytesRead = await _pipeClient.ReadAsync(lengthBuffer, 0, 4);
                    if (bytesRead == 0) { POpsHelpers.Log("VISION", "Ajan tarafı bağlantıyı kapattı.", true); break; }

                    int dataLength = BitConverter.ToInt32(lengthBuffer, 0);
                    byte[] dataBuffer = new byte[dataLength];
                    int totalRead = 0;

                    while (totalRead < dataLength)
                    {
                        int read = await _pipeClient.ReadAsync(dataBuffer, totalRead, dataLength - totalRead);
                        if (read == 0) break;
                        totalRead += read;
                    }

                    if (totalRead == dataLength)
                    {
                        string command = Encoding.UTF8.GetString(dataBuffer);

                        if (command == "START_CAPTURE") { _isCapturing = true; }
                        else if (command == "STOP_CAPTURE") { _isCapturing = false; }
                        else if (command == "CAPTURE_SNAPSHOT") { _ = Task.Run(async () => await CaptureAndSendSnapshotAsync()); }
                        else if (command.Contains("remote_input")) { ProcessRemoteInput(command); }
                    }
                }
            }
            catch (Exception ex) { POpsHelpers.Log("VISION", $"Komut dinleme hatası: {ex.Message}", true); }
        }

        static async Task CaptureAndSendSnapshotAsync()
        {
            try
            {
                byte[] jpegBytes = CaptureFrameToJpeg();
                if (jpegBytes != null)
                {
                    await SendFrameToPipeAsync(jpegBytes);
                }
            }
            catch { }
        }

        static async Task CaptureAndStreamAsync()
        {
            try
            {
                while (_pipeClient != null && _pipeClient.IsConnected)
                {
                    if (!_isCapturing) { await Task.Delay(100); continue; }

                    Stopwatch sw = Stopwatch.StartNew();
                    byte[] jpegBytes = CaptureFrameToJpeg();

                    if (jpegBytes != null) await SendFrameToPipeAsync(jpegBytes);

                    sw.Stop();
                    // 🚀 FPS KONTROLÜ BURADA YAPILIYOR
                    int targetDelayMs = 1000 / _targetFps;
                    int timeToSleep = targetDelayMs - (int)sw.ElapsedMilliseconds;

                    if (timeToSleep > 0) await Task.Delay(timeToSleep);
                    else await Task.Delay(5);
                }
            }
            catch { }
        }

        static byte[] CaptureFrameToJpeg()
        {
            try
            {
                var dxgiFrame = _screenEngine.GetNextFrameAsJpeg();
                if (dxgiFrame != null) return dxgiFrame;

                int screenWidth = GetSystemMetrics(0);
                int screenHeight = GetSystemMetrics(1);
                if (screenWidth <= 0 || screenHeight <= 0) return null;

                int targetWidth = (int)(screenWidth * _scaleFactor);
                int targetHeight = (int)(screenHeight * _scaleFactor);

                using (Bitmap bmpScreen = new Bitmap(screenWidth, screenHeight))
                {
                    using (Graphics gScreen = Graphics.FromImage(bmpScreen))
                    {
                        gScreen.CopyFromScreen(0, 0, 0, 0, bmpScreen.Size, CopyPixelOperation.SourceCopy);
                        DrawCursor(gScreen);
                    }

                    using (Bitmap bmpScaled = new Bitmap(targetWidth, targetHeight))
                    using (Graphics gScaled = Graphics.FromImage(bmpScaled))
                    {
                        gScaled.InterpolationMode = InterpolationMode.Bilinear;
                        gScaled.DrawImage(bmpScreen, 0, 0, targetWidth, targetHeight);
                        using (MemoryStream ms = new MemoryStream())
                        {
                            bmpScaled.Save(ms, _jpegEncoder, _encoderParameters);
                            return ms.ToArray();
                        }
                    }
                }
            }
            catch { return null; }
        }

        static async Task SendFrameToPipeAsync(byte[] jpegBytes)
        {
            if (_pipeClient == null || !_pipeClient.IsConnected || jpegBytes == null) return;
            await _pipeWriteLock.WaitAsync();
            try
            {
                if (!_pipeClient.IsConnected) return;
                byte[] lengthPrefix = BitConverter.GetBytes(jpegBytes.Length);
                await _pipeClient.WriteAsync(lengthPrefix, 0, 4);
                await _pipeClient.WriteAsync(jpegBytes, 0, jpegBytes.Length);
                await _pipeClient.FlushAsync();
            }
            catch { }
            finally { _pipeWriteLock.Release(); }
        }

        static void ProcessRemoteInput(string jsonPayload)
        {
            try
            {
                using var doc = JsonDocument.Parse(jsonPayload);
                var root = doc.RootElement;

                if (root.TryGetProperty("type", out var typeProp) && typeProp.GetString() == "remote_input")
                {
                    // 🚀 EKSİK OLAN FPS YAKALAMA MANTIĞI BURAYA EKLENDİ!
                    if (root.TryGetProperty("action", out var actionProp) && actionProp.GetString() == "set_fps")
                    {
                        if (root.TryGetProperty("fps", out var fpsProp))
                        {
                            _targetFps = fpsProp.GetInt32();
                            POpsHelpers.Log("VISION", $"Motor Hızı (FPS) Güncellendi: {_targetFps}");
                        }
                        return; // İşlem FPS değiştirmekse alttaki fare/klavye kodlarını çalıştırma
                    }

                    string inputType = root.TryGetProperty("input_type", out var itProp) ? itProp.GetString() : "";

                    switch (inputType)
                    {
                        case "mouse_move": HandleMouseMove(root); break;
                        case "mouse_click": HandleMouseClick(root); break;
                        case "mouse_wheel": HandleMouseWheel(root); break;
                        case "keyboard": HandleKeyboard(root); break;
                    }
                }
            }
            catch (Exception ex) { POpsHelpers.Log("VISION_INPUT", $"Input Parse Hatası: {ex.Message}", true); }
        }

        static void HandleMouseMove(JsonElement data)
        {
            try
            {
                int x = data.TryGetProperty("x", out var xProp) ? xProp.GetInt32() : 0;
                int y = data.TryGetProperty("y", out var yProp) ? yProp.GetInt32() : 0;
                SetCursorPos(x, y);
            }
            catch (Exception ex) { POpsHelpers.Log("VISION_INPUT", $"Fare Işınlama Hatası: {ex.Message}", true); }
        }

        static void HandleMouseClick(JsonElement data)
        {
            try
            {
                string button = data.TryGetProperty("button", out var btnProp) ? btnProp.GetString() : "left";
                bool isDown = data.TryGetProperty("is_down", out var isDownProp) && isDownProp.GetBoolean();

                uint flag = 0;
                if (button == "left") flag = isDown ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP;
                else if (button == "right") flag = isDown ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP;
                else if (button == "middle") flag = isDown ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP;

                if (flag != 0) SendMouseEvent(flag, 0);
            }
            catch (Exception ex) { POpsHelpers.Log("VISION_INPUT", $"Fare Tıklama Hatası: {ex.Message}", true); }
        }

        static void HandleMouseWheel(JsonElement data)
        {
            try
            {
                int delta = data.TryGetProperty("delta", out var deltaProp) ? deltaProp.GetInt32() : 0;
                SendMouseEvent(MOUSEEVENTF_WHEEL, (uint)delta);
            }
            catch { }
        }

        static void SendMouseEvent(uint flags, uint data)
        {
            INPUT[] inputs = new INPUT[1];
            inputs[0].type = INPUT_MOUSE;
            inputs[0].u.mi.dwFlags = flags;
            inputs[0].u.mi.mouseData = data;
            SendInput(1, inputs, Marshal.SizeOf(typeof(INPUT)));
        }

        static void HandleKeyboard(JsonElement data)
        {
            try
            {
                string key = data.TryGetProperty("key", out var keyProp) ? keyProp.GetString() : "";
                bool isDown = data.TryGetProperty("is_down", out var keyDownProp) && keyDownProp.GetBoolean();

                byte vk = GetVirtualKeyCode(key);
                if (vk == 0) return; // Tanımsız bir tuşsa yoksay

                uint flags = isDown ? KEYEVENTF_KEYDOWN : KEYEVENTF_KEYUP;
                if (IsExtendedKey(vk)) flags |= KEYEVENTF_EXTENDEDKEY;

                INPUT[] inputs = new INPUT[1];
                inputs[0].type = INPUT_KEYBOARD;
                inputs[0].u.ki.wVk = vk;
                inputs[0].u.ki.dwFlags = flags;
                SendInput(1, inputs, Marshal.SizeOf(typeof(INPUT)));
            }
            catch (Exception ex) { POpsHelpers.Log("VISION_INPUT", $"Klavye Hatası: {ex.Message}", true); }
        }

        // 🚀 TAM KAPSAMLI AKILLI TUŞ HARİTASI
        static byte GetVirtualKeyCode(string key)
        {
            if (string.IsNullOrEmpty(key)) return 0;

            // Önce JavaScript'in özel komut isimlerini yakalayalım (Kombinasyonlar dahil)
            switch (key)
            {
                case "Backspace": return 0x08;
                case "Tab": return 0x09;
                case "Enter": return 0x0D;
                case "Shift": return 0x10;
                case "Control": return 0x11;
                case "Alt": return 0x12;
                case "Pause": return 0x13;
                case "CapsLock": return 0x14;
                case "Escape": return 0x1B;
                case " ": return 0x20; // Space (Boşluk Tuşu)
                case "PageUp": return 0x21;
                case "PageDown": return 0x22;
                case "End": return 0x23;
                case "Home": return 0x24;
                case "ArrowLeft": return 0x25;
                case "ArrowUp": return 0x26;
                case "ArrowRight": return 0x27;
                case "ArrowDown": return 0x28;
                case "Insert": return 0x2D;
                case "Delete": return 0x2E;
                case "Meta": return 0x5B; // Windows Tuşu
                case "ContextMenu": return 0x5D;
                case "F1": return 0x70;
                case "F2": return 0x71;
                case "F3": return 0x72;
                case "F4": return 0x73;
                case "F5": return 0x74;
                case "F6": return 0x75;
                case "F7": return 0x76;
                case "F8": return 0x77;
                case "F9": return 0x78;
                case "F10": return 0x79;
                case "F11": return 0x7A;
                case "F12": return 0x7B;
                case "NumLock": return 0x90;
                case "ScrollLock": return 0x91;
            }

            // Geriye kalan her şey için (A-Z, 0-9, Türkçe Karakterler, Noktalama)
            // Uzak bilgisayarın klavye diline göre otomatik bulur!
            if (key.Length == 1)
            {
                short vk = VkKeyScan(key[0]);
                if (vk != -1)
                {
                    return (byte)(vk & 0xFF);
                }
            }

            return 0;
        }

        static bool IsExtendedKey(byte vk)
        {
            return vk == 0x25 || vk == 0x27 || vk == 0x26 || vk == 0x28 || vk == 0x2D || vk == 0x2E || vk == 0x24 || vk == 0x23 || vk == 0x21 || vk == 0x22 || vk == 0x5B || vk == 0x5C;
        }

        static ImageCodecInfo GetEncoder(ImageFormat format) => ImageCodecInfo.GetImageDecoders().FirstOrDefault(codec => codec.FormatID == format.Guid);

        [StructLayout(LayoutKind.Sequential)] struct CURSORINFO { public int cbSize; public int flags; public IntPtr hCursor; public Point ptScreenPos; }
        [DllImport("user32.dll")] static extern bool GetCursorInfo(out CURSORINFO pci);
        [DllImport("user32.dll")] static extern bool DrawIcon(IntPtr hDC, int X, int Y, IntPtr hIcon);
        [DllImport("user32.dll")] static extern IntPtr CopyIcon(IntPtr hIcon);
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr hIcon);

        const int CURSOR_SHOWING = 0x00000001;

        static void DrawCursor(Graphics g)
        {
            try
            {
                CURSORINFO ci = new CURSORINFO();
                ci.cbSize = Marshal.SizeOf(typeof(CURSORINFO));
                if (GetCursorInfo(out ci) && ci.flags == CURSOR_SHOWING)
                {
                    IntPtr hIcon = CopyIcon(ci.hCursor);
                    if (hIcon != IntPtr.Zero)
                    {
                        DrawIcon(g.GetHdc(), ci.ptScreenPos.X, ci.ptScreenPos.Y, hIcon);
                        g.ReleaseHdc();
                        DestroyIcon(hIcon);
                    }
                }
            }
            catch { }
        }
    }
}