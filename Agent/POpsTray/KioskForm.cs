using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;

namespace POpsTray
{
    public class KioskForm : Form
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;

        private static LowLevelKeyboardProc _proc = HookCallback;
        private static IntPtr _hookID = IntPtr.Zero;

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        private string _reason;
        // Yönetici bypass kodu servise gönderilir (UNLOCK_BYPASS); servis doğrular, hatalı denemeleri kilitler
        // Dönen: kod servise ulaştı mı
        private readonly Func<string, bool> _submitBypass;
        private TextBox _txtBypass = null!;
        private Label _lblBypassStatus = null!;
        // "Kod doğrulanıyor…" servis yanıt vermezse 10 sn sonra silinir
        private readonly Timer _statusTimer = new Timer { Interval = 10000 };

        public const string CodeFormatHint = "Kod en az 6 karakterdir ve yalnızca 0-9 rakamları ile A-F harflerinden oluşur (O harfi değil 0 rakamı).";

        public const string ServiceUnreachable = "POps servisine ulaşılamadı; kod gönderilmedi. Birkaç saniye sonra yeniden deneyin.";

        public KioskForm(string reason, Func<string, bool> submitBypass)
        {
            _reason = reason;
            _submitBypass = submitBypass;
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            this.FormBorderStyle = FormBorderStyle.None;
            this.WindowState = FormWindowState.Maximized;
            this.TopMost = true;
            this.ShowInTaskbar = false;
            this.BackColor = Color.FromArgb(10, 10, 10);
            this.DoubleBuffered = true;

            TableLayoutPanel table = new TableLayoutPanel();
            table.Dock = DockStyle.Fill;
            table.RowCount = 3;
            table.ColumnCount = 1;
            table.RowStyles.Add(new RowStyle(SizeType.Percent, 35F));
            table.RowStyles.Add(new RowStyle(SizeType.Percent, 30F));
            table.RowStyles.Add(new RowStyle(SizeType.Percent, 35F));

            Label lblIcon = new Label();
            lblIcon.Text = "⚠";
            lblIcon.ForeColor = Color.Red;
            lblIcon.Font = new Font("Segoe UI", 120, FontStyle.Bold);
            lblIcon.TextAlign = ContentAlignment.BottomCenter;
            lblIcon.Dock = DockStyle.Fill;

            Label lblWarning = new Label();
            lblWarning.Text = $"BİLGİSAYAR ERİŞİMİNİZ KISITLANDI VE KARANTİNAYA ALINDI!\n\nNeden: {_reason}\n\nBu işlem kurumsal güvenlik prosedürüdür.\nBilgisayarı fişten çekmek, kapatmaya çalışmak veya ağ bağlantısını kesmek güvenlik ihlali sayılacak ve idari işlem başlatılacaktır.";
            lblWarning.ForeColor = Color.White;
            lblWarning.Font = new Font("Segoe UI", 24, FontStyle.Bold);
            lblWarning.TextAlign = ContentAlignment.MiddleCenter;
            lblWarning.Dock = DockStyle.Fill;

            Label lblSubText = new Label();
            lblSubText.Text = "Yönetici izni olmadan bu kilit ekranı kaldırılamaz.";
            lblSubText.ForeColor = Color.LightGray;
            lblSubText.Font = new Font("Segoe UI", 16, FontStyle.Regular);
            lblSubText.TextAlign = ContentAlignment.TopCenter;
            lblSubText.AutoSize = true;
            lblSubText.Anchor = AnchorStyles.Top;

            // Kilit ekranı görev çubuğunu da kapattığı için tepsi menüsündeki bypass girişine ulaşılamaz: çevrimdışı
            // bypass kodu burada girilir. Geçerliyse servis kilit ekranını kapatır ve ağ yalıtımını kaldırır.
            var bypassRow = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Anchor = AnchorStyles.Top,
                Margin = new Padding(0, 24, 0, 0),
            };
            var lblBypass = new Label { Text = "Yönetici bypass kodu:", ForeColor = Color.LightGray, Font = new Font("Segoe UI", 12), AutoSize = true, Margin = new Padding(0, 6, 8, 0) };
            _txtBypass = new TextBox { Width = 180, Font = new Font("Segoe UI", 12), CharacterCasing = CharacterCasing.Upper, MaxLength = 64 };
            var btnBypass = new Button { Text = "Kilidi Aç", AutoSize = true, Font = new Font("Segoe UI", 11), ForeColor = Color.White, BackColor = Color.FromArgb(60, 60, 60), FlatStyle = FlatStyle.Flat, Margin = new Padding(8, 0, 0, 0) };
            btnBypass.Click += (s, e) => SubmitBypass();
            this.AcceptButton = btnBypass;
            bypassRow.Controls.AddRange(new Control[] { lblBypass, _txtBypass, btnBypass });

            _lblBypassStatus = new Label { Text = "", ForeColor = Color.Orange, Font = new Font("Segoe UI", 11), AutoSize = true, Anchor = AnchorStyles.Top, Margin = new Padding(0, 10, 0, 0) };

            TableLayoutPanel bottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            bottom.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            bottom.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            bottom.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            bottom.Controls.Add(lblSubText, 0, 0);
            bottom.Controls.Add(bypassRow, 0, 1);
            bottom.Controls.Add(_lblBypassStatus, 0, 2);

            table.Controls.Add(lblIcon, 0, 0);
            table.Controls.Add(lblWarning, 0, 1);
            table.Controls.Add(bottom, 0, 2);
            
            this.Controls.Add(table);

            this.FormClosing += KioskForm_FormClosing;
            _statusTimer.Tick += (s, e) =>
            {
                _statusTimer.Stop();
                _lblBypassStatus.Text = "";
            };
        }

        // Yalnızca çalışma anında kullanılan bayrak: tasarımcıda görünmez, serileştirilmez (.NET 10 WFO1000)
        [System.ComponentModel.Browsable(false)]
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool AllowClose { get; set; } = false;

        private void SubmitBypass()
        {
            string code = _txtBypass.Text.Trim();
            if (code.Length == 0) return;
            // Biçim hatası servise gitmez: deneme hakkı yemez
            if (!POps.Shared.BypassCode.IsWellFormed(code))
            {
                ShowStatus(CodeFormatHint, Color.Orange);
                return;
            }
            _txtBypass.Clear();
            // Servis bağlantısı yoksa kod sessizce kaybolmaz: kullanıcı yeniden dener
            if (_submitBypass?.Invoke(code) != true)
            {
                ShowStatus(ServiceUnreachable, Color.Orange);
                return;
            }
            ShowStatus("Kod doğrulanıyor…", Color.LightGray, clearAfterTimeout: true);
        }

        private void ShowStatus(string text, Color color, bool clearAfterTimeout = false)
        {
            _statusTimer.Stop();
            _lblBypassStatus.ForeColor = color;
            _lblBypassStatus.Text = text;
            if (clearAfterTimeout) _statusTimer.Start();
        }

        // Servis kodu reddetti (hatalı ya da çok sayıda denemeden sonra bypass geçici olarak kilitli)
        public void ShowBypassRejected() =>
            ShowStatus("Kod kabul edilmedi. Art arda hatalı denemelerden sonra bypass bir süre kilitlenir.", Color.Orange);

        // Kod ya da panel kilidi kaldırmak istedi ama ağ yalıtımı kaldırılamadı
        public void ShowUnlockFailed() =>
            ShowStatus("Ağ yalıtımı kaldırılamadı; kilit sürüyor. BT yöneticisine bildirin.", Color.Orange);

        private void KioskForm_FormClosing(object? sender, FormClosingEventArgs e)
        {
            // Block closing via Alt+F4 or Task Manager directly if we haven't manually authorized it
            if (!AllowClose)
            {
                e.Cancel = true;
            }
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            _hookID = SetHook(_proc);
            
            // En üstte kalır ve odağı geri alır: Ctrl+Shift+Esc, Win tuşu, Alt+Tab ya da başka bir pencere öne geçse de
            // en geç yarım saniyede kilit ekranı yeniden öne gelir
            Timer t = new Timer();
            t.Interval = 500;
            t.Tick += (s, ev) =>
            {
                this.TopMost = true;
                TakeForeground();
            };
            t.Start();
            this.Deactivate += (s, ev) => { if (!AllowClose) BeginInvoke(new Action(TakeForeground)); };
        }

        // Windows başka sürecin önündeki pencereye odak vermeyi kısıtlar: öndeki pencerenin iş parçacığına geçici
        // bağlanılarak (AttachThreadInput) odak alınır
        private void TakeForeground()
        {
            if (AllowClose || IsDisposed || !IsHandleCreated) return;
            IntPtr foreground = GetForegroundWindow();
            if (foreground == this.Handle) return;
            uint foregroundThread = GetWindowThreadProcessId(foreground, IntPtr.Zero);
            uint me = GetCurrentThreadId();
            bool attached = foregroundThread != 0 && foregroundThread != me && AttachThreadInput(me, foregroundThread, true);
            try
            {
                SetForegroundWindow(this.Handle);
                this.BringToFront();
                this.Activate();
            }
            finally
            {
                if (attached) AttachThreadInput(me, foregroundThread, false);
            }
        }

        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);

        // OnClosed eskidi (WFDEV004); aynı kapanışta OnFormClosed çağrılır
        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _statusTimer.Dispose();
            UnhookWindowsHookEx(_hookID);
            base.OnFormClosed(e);
        }

        private static IntPtr SetHook(LowLevelKeyboardProc proc)
        {
            using (Process curProcess = Process.GetCurrentProcess())
            using (ProcessModule curModule = curProcess.MainModule!)
            {
                return SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(curModule.ModuleName), 0);
            }
        }

        private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
            {
                int vkCode = Marshal.ReadInt32(lParam);

                // LWIN, RWIN
                if (vkCode == 0x5B || vkCode == 0x5C) return (IntPtr)1;

                // Tab, Esc (for Alt+Tab, Ctrl+Esc)
                if (vkCode == 0x09 || vkCode == 0x1B) return (IntPtr)1;

                // F4 (for Alt+F4)
                if (vkCode == 0x73) return (IntPtr)1;
            }
            return CallNextHookEx(_hookID, nCode, wParam, lParam);
        }
        
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                // ExStyle WS_EX_TOOLWINDOW
                cp.ExStyle |= 0x80;
                return cp;
            }
        }
    }
}
