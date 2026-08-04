using System;
using System.Drawing;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;

namespace POpsTray
{
    public class CountdownForm : Form
    {
        private int _countdownSeconds;
        private string _reason;
        private bool _isQuarantined;
        private Label _lblWarning;
        private Label _lblTimer;
        private Label _lblReason;
        private Timer _timer;
        private Action _onComplete;

        public CountdownForm(int countdownSeconds, string reason, bool isQuarantined, Action onComplete)
        {
            _countdownSeconds = countdownSeconds;
            _reason = reason;
            _isQuarantined = isQuarantined;
            _onComplete = onComplete;
            
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            this.FormBorderStyle = FormBorderStyle.None;
            this.WindowState = FormWindowState.Maximized;
            this.TopMost = true;
            this.ShowInTaskbar = false;
            this.BackColor = Color.Black;
            this.Opacity = 0.85;
            this.DoubleBuffered = true;

            TableLayoutPanel table = new TableLayoutPanel();
            table.Dock = DockStyle.Fill;
            table.RowCount = 3;
            table.ColumnCount = 1;
            table.RowStyles.Add(new RowStyle(SizeType.Percent, 30F));
            table.RowStyles.Add(new RowStyle(SizeType.Percent, 40F));
            table.RowStyles.Add(new RowStyle(SizeType.Percent, 30F));

            string warningText = _isQuarantined 
                ? "DİKKAT: BİLGİSAYAR ERİŞİMİNİZ KISITLANDI VE KARANTİNAYA ALINDI!" 
                : "BİLGİ İŞLEM ZORUNLU MÜDAHALE BİLDİRİMİ";
            
            string subText = _isQuarantined 
                ? "DİKKAT: Bilgisayarı kapatmak veya ağ bağlantısını kesmek güvenlik ihlali sayılacaktır ve idari işlem başlatılacaktır." 
                : "Lütfen bekleyiniz. Cihazınızı kapatmayınız.";

            _lblWarning = new Label();
            _lblWarning.Text = $"{warningText}\n\nNeden: {_reason}\n\n{subText}";
            _lblWarning.ForeColor = _isQuarantined ? Color.Red : Color.Yellow;
            _lblWarning.Font = new Font("Segoe UI", 24, FontStyle.Bold);
            _lblWarning.TextAlign = ContentAlignment.BottomCenter;
            _lblWarning.Dock = DockStyle.Fill;

            _lblTimer = new Label();
            _lblTimer.Text = _countdownSeconds.ToString();
            _lblTimer.ForeColor = Color.White;
            _lblTimer.Font = new Font("Segoe UI", 120, FontStyle.Bold);
            _lblTimer.TextAlign = ContentAlignment.MiddleCenter;
            _lblTimer.Dock = DockStyle.Fill;

            _lblReason = new Label();
            _lblReason.Text = "Yönetici bağlantısı kuruluyor...";
            _lblReason.ForeColor = Color.White;
            _lblReason.Font = new Font("Segoe UI", 18, FontStyle.Regular);
            _lblReason.TextAlign = ContentAlignment.TopCenter;
            _lblReason.Dock = DockStyle.Fill;

            table.Controls.Add(_lblWarning, 0, 0);
            table.Controls.Add(_lblTimer, 0, 1);
            table.Controls.Add(_lblReason, 0, 2);
            
            this.Controls.Add(table);

            _timer = new Timer();
            _timer.Interval = 1000;
            _timer.Tick += Timer_Tick;
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            _timer.Start();
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            _countdownSeconds--;
            if (_countdownSeconds <= 0)
            {
                _timer.Stop();
                this.Hide();
                _onComplete?.Invoke();
                this.Close();
            }
            else
            {
                _lblTimer.Text = _countdownSeconds.ToString();
            }
        }
        
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                // ExStyle WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW
                cp.ExStyle |= 0x20 | 0x80;
                return cp;
            }
        }
    }
}
