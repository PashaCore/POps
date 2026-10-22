using System.Drawing;
using System.Windows.Forms;

namespace POpsTray;

// Sınav modu bandı: birincil ekranın üstünde, her zaman üstte, odağı almayan ince bir şerit. Kullanıcı kapatamaz;
// servis sınav modunu bitirince kapanır.
internal sealed class ExamBanner : Form
{
    private const int WS_EX_TOPMOST = 0x00000008, WS_EX_TOOLWINDOW = 0x00000080, WS_EX_NOACTIVATE = 0x08000000;
    private readonly Label _label;

    public ExamBanner()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(150, 20, 20);
        Height = 30;
        _label = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 10f, FontStyle.Bold),
            AutoEllipsis = true,
        };
        Controls.Add(_label);
    }

    // Kapatma yalnızca servis sınavı bitirince (çalışma anına özel; tasarımcıda görünmez, serileştirilmez)
    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool AllowClose { get; set; }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    public void ShowMessage(string message, long? until)
    {
        string end = until == null ? "" : "  ·  bitiş " + System.DateTimeOffset.FromUnixTimeSeconds(until.Value).ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        _label.Text = message + end;
        Rectangle area = (Screen.PrimaryScreen ?? Screen.AllScreens[0]).WorkingArea;
        Width = System.Math.Min(900, area.Width - 40);
        Location = new Point(area.Left + (area.Width - Width) / 2, area.Top);
        if (!Visible) Show();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!AllowClose && e.CloseReason == CloseReason.UserClosing) e.Cancel = true;
        base.OnFormClosing(e);
    }
}
