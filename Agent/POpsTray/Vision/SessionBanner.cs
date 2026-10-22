using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace POpsTray.Vision;

// Bilgisayar kilitliyken başlayan Vision oturumunun bildirimi (docs/vision.md, karar 5): birincil ekranın altında, her
// zaman üstte, odağı almayan bir şerit. Kullanıcı kapatamaz; oturum bitince (servis VISION_NOTICE_OFF ya da STOP_CAPTURE
// gönderir ya da servis bağlantısı koparsa) tepsi kapatır. Sınav modu bandı üstte olduğu için bu alttadır. Ekrandaki her
// şey gibi yöneticinin gördüğü görüntüde de vardır.
internal sealed class SessionBanner : Form
{
    private const int WS_EX_TOPMOST = 0x00000008, WS_EX_TOOLWINDOW = 0x00000080, WS_EX_NOACTIVATE = 0x08000000;
    private readonly Label _label;

    public SessionBanner()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(170, 70, 0);
        Height = 48;
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

    // Kapatma yalnızca oturum bitince (çalışma anına özel; tasarımcıda görünmez, serileştirilmez)
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
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

    // Şerit ekrana konur ve hemen çizilir (tepsi yakalamayı ancak bundan sonra serbest bırakır)
    public void ShowMessage(string message)
    {
        _label.Text = message;
        Rectangle area = (Screen.PrimaryScreen ?? Screen.AllScreens[0]).WorkingArea;
        Width = System.Math.Min(1000, area.Width - 40);
        Location = new Point(area.Left + (area.Width - Width) / 2, area.Bottom - Height);
        if (!Visible) Show();
        Refresh();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!AllowClose && e.CloseReason == CloseReason.UserClosing) e.Cancel = true;
        base.OnFormClosing(e);
    }
}
