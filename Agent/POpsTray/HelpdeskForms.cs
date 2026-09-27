using System.Drawing;
using System.Text;
using System.Text.Json;
using Timer = System.Windows.Forms.Timer;

namespace POpsTray;

// Yardım masası: tepsi yalnızca formu gösterir; talep servise boru üzerinden gider, servis oturumdaki kullanıcıyı
// ekleyip sunucuya iletir (bkz. servis Helpdesk). Mesajlar "TÜR:<base64 JSON>" biçimindedir.
internal static class HelpdeskProtocol
{
    public static string Encode(object value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)));

    public static JsonDocument? Decode(string encoded)
    {
        try { return JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(encoded))); }
        catch { return null; }
    }

    public static string Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    // Sunucunun ISO 8601 zamanı -> yerel "gg.aa.yyyy SS:dd"
    public static string LocalTime(string iso) =>
        DateTimeOffset.TryParse(iso, out var t) ? t.ToLocalTime().ToString("dd.MM.yyyy HH:mm") : "";
}

internal sealed class ReportProblemForm : Form
{
    public const int MaxSubject = 200, MaxBody = 5000;

    // Sunucudaki kategori kodları (Backend/pops/routers/helpdesk.py CATEGORIES)
    private static readonly (string Text, string Code)[] Categories =
    {
        ("Donanım (bilgisayar, ekran, fare, klavye)", "donanim"),
        ("Yazılım / program", "yazilim"),
        ("Ağ / internet", "ag"),
        ("Yazıcı", "yazici"),
        ("Hesap / şifre", "hesap"),
        ("Diğer", "diger"),
    };

    private readonly Action<string> _send;
    private readonly TextBox _subject = new TextBox { MaxLength = MaxSubject, Dock = DockStyle.Fill };
    private readonly ComboBox _category = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly TextBox _body = new TextBox { MaxLength = MaxBody, Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, AcceptsReturn = true };
    private readonly Button _submit = new Button { Text = "Gönder", AutoSize = true };
    private readonly Label _status = new Label { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(440, 0) };
    // Servis yanıt vermezse form kilitli kalmasın
    private readonly Timer _timeout = new Timer { Interval = 30000 };
    private bool _done;

    public ReportProblemForm(Action<string> send)
    {
        _send = send;
        Text = "POps - Sorun bildir";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(480, 420);
        Font = new Font("Segoe UI", 9.5F);

        foreach (var (text, _) in Categories) _category.Items.Add(text);
        _category.SelectedIndex = Categories.Length - 1;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 8 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = "Konu (en az 3 karakter)", AutoSize = true }, 0, 0);
        layout.Controls.Add(_subject, 0, 1);
        layout.Controls.Add(new Label { Text = "Kategori", AutoSize = true, Margin = new Padding(3, 8, 3, 3) }, 0, 2);
        layout.Controls.Add(_category, 0, 3);
        layout.Controls.Add(new Label { Text = "Açıklama (ne oldu, hangi programda, hata mesajı)", AutoSize = true, Margin = new Padding(3, 8, 3, 3) }, 0, 4);
        layout.Controls.Add(_body, 0, 5);
        layout.Controls.Add(_status, 0, 6);
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, AutoSize = true };
        var cancel = new Button { Text = "Kapat", AutoSize = true };
        cancel.Click += (_, _) => Close();
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(_submit);
        layout.Controls.Add(buttons, 0, 7);
        Controls.Add(layout);

        _submit.Click += (_, _) => Submit();
        _timeout.Tick += (_, _) =>
        {
            _timeout.Stop();
            SetStatus("Yanıt alınamadı; POps servisi çalışmıyor olabilir. Biraz sonra yeniden deneyin.", Color.DarkOrange);
            SetEditable(true);
        };
        FormClosed += (_, _) => _timeout.Dispose();
    }

    private void Submit()
    {
        string subject = _subject.Text.Trim();
        if (subject.Length < 3)
        {
            SetStatus("Konu en az 3 karakter olmalı.", Color.DarkOrange);
            _subject.Focus();
            return;
        }
        string category = Categories[Math.Max(0, _category.SelectedIndex)].Code;
        SetEditable(false);
        SetStatus("Gönderiliyor…", Color.DimGray);
        _timeout.Start();
        _send("TICKET_CREATE:" + HelpdeskProtocol.Encode(new { subject, category, body = _body.Text.Trim() }));
    }

    // Servisin yanıtı (TICKET_RESULT)
    public void ShowResult(JsonElement result)
    {
        _timeout.Stop();
        bool ok = result.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True;
        string message = HelpdeskProtocol.Text(result, "message");
        if (ok)
        {
            _done = true;
            SetStatus(message, Color.ForestGreen);
            _submit.Visible = false;
        }
        else
        {
            SetStatus(message, Color.DarkOrange);
            SetEditable(true);
        }
    }

    private void SetEditable(bool editable)
    {
        if (_done) editable = false;
        _subject.Enabled = _category.Enabled = _body.Enabled = _submit.Enabled = editable;
    }

    private void SetStatus(string text, Color color)
    {
        _status.ForeColor = color;
        _status.Text = text;
    }
}

internal sealed class MyTicketsForm : Form
{
    private readonly Action<string> _send;
    private readonly RichTextBox _view = new RichTextBox { ReadOnly = true, Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, BackColor = SystemColors.Window };
    private readonly Label _status = new Label { AutoSize = true, ForeColor = Color.DimGray };

    public MyTicketsForm(Action<string> send)
    {
        _send = send;
        Text = "POps - Taleplerim";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(560, 480);
        Font = new Font("Segoe UI", 9.5F);

        var refresh = new Button { Text = "Yenile", AutoSize = true };
        refresh.Click += (_, _) => Request();
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(8) };
        bottom.Controls.Add(refresh);
        bottom.Controls.Add(_status);
        var viewHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 10, 12, 10), BackColor = SystemColors.Window };
        viewHost.Controls.Add(_view);
        Controls.Add(viewHost);
        Controls.Add(bottom);
        Shown += (_, _) => Request();
    }

    public void Request()
    {
        _status.Text = "Talepler alınıyor…";
        _send("TICKET_LIST");
    }

    // Servisin yanıtı (TICKET_LIST_RESULT): yalnızca oturumdaki kullanıcının talepleri
    public void ShowTickets(JsonElement result)
    {
        _view.Clear();
        string message = HelpdeskProtocol.Text(result, "message");
        _status.Text = message;
        if (!result.TryGetProperty("tickets", out var tickets) || tickets.ValueKind != JsonValueKind.Array) return;
        var bold = new Font(_view.Font, FontStyle.Bold);
        foreach (JsonElement t in tickets.EnumerateArray())
        {
            long id = t.TryGetProperty("id", out var i) && i.TryGetInt64(out long n) ? n : 0;
            _view.SelectionFont = bold;
            _view.AppendText($"#{id} · {HelpdeskProtocol.Text(t, "subject")}\n");
            _view.SelectionFont = _view.Font;
            _view.AppendText($"Durum: {HelpdeskProtocol.Text(t, "status_text")} · Açılış: {HelpdeskProtocol.LocalTime(HelpdeskProtocol.Text(t, "created_at"))}\n");
            if (t.TryGetProperty("replies", out var replies) && replies.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement r in replies.EnumerateArray())
                    _view.AppendText($"   {HelpdeskProtocol.Text(r, "author")} ({HelpdeskProtocol.LocalTime(HelpdeskProtocol.Text(r, "created_at"))}): {HelpdeskProtocol.Text(r, "body")}\n");
            }
            _view.AppendText("\n");
        }
        _view.SelectionStart = 0;
    }
}
