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

        public KioskForm(string reason)
        {
            _reason = reason;
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
            lblSubText.Dock = DockStyle.Fill;

            table.Controls.Add(lblIcon, 0, 0);
            table.Controls.Add(lblWarning, 0, 1);
            table.Controls.Add(lblSubText, 0, 2);
            
            this.Controls.Add(table);

            this.FormClosing += KioskForm_FormClosing;
        }

        public bool AllowClose { get; set; } = false;

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
            
            // Keep enforcing TopMost aggressively
            Timer t = new Timer();
            t.Interval = 1000;
            t.Tick += (s, ev) => 
            {
                this.TopMost = true;
                this.BringToFront();
            };
            t.Start();
        }

        protected override void OnClosed(EventArgs e)
        {
            UnhookWindowsHookEx(_hookID);
            base.OnClosed(e);
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
