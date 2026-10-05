using System.Drawing;
using System.Globalization;
using System.Media;
using System.Text.Json;
using Timer = System.Windows.Forms.Timer;

namespace POpsTray;

// Uzaktan güç işlemi geri sayımı ve kullanıcıya mesaj. Servis mesajları (boru):
//   POWER_COUNTDOWN:<base64 JSON {task_id, op, seconds, message}>  geri sayım penceresi (yenisi eskisinin yerine geçer)
//   POWER_CANCEL:<task_id>                                          geri sayım iptal edildi
//   POWER_LOCK:<task_id>                                            LockWorkStation; yanıt POWER_LOCK_RESULT:<task_id>:1|0
//   USER_MESSAGE:<base64 JSON {task_id, title, text, style, requires_ack}>
//   USER_MESSAGE_CLOSE:<task_id>                                    mesaj geri çekildi
// Kullanıcı okundu onayı isteyen mesajda Tamam'a basınca servise USER_MESSAGE_ACK:<task_id> gider. İşlemi servis
// yapar (zamanlayıcı serviste); pencereler yalnızca bilgi verir ve odağı çalmaz (yazarken Enter mesajı onaylamasın).
public partial class MainForm
{
    private PowerCountdownForm? _countdownForm;
    private readonly Dictionary<int, UserMessageForm> _messageForms = new Dictionary<int, UserMessageForm>();

    // Dönen: mesaj güç/mesaj komutuydu
    private bool HandlePowerAndMessages(string message)
    {
        if (message.StartsWith("POWER_COUNTDOWN:", StringComparison.Ordinal))
        {
            using JsonDocument? doc = HelpdeskProtocol.Decode(message.Substring("POWER_COUNTDOWN:".Length));
            if (doc == null || !TaskIdOf(doc.RootElement, out int taskId)) return true;
            JsonElement root = doc.RootElement;
            string op = HelpdeskProtocol.Text(root, "op");
            int seconds = root.TryGetProperty("seconds", out JsonElement s) && s.TryGetInt32(out int value) ? Math.Clamp(value, 0, 600) : 0;
            string note = Shorten(HelpdeskProtocol.Text(root, "message"), 200);
            this.Invoke(new Action(() =>
            {
                CloseCountdown(null);
                _countdownForm = new PowerCountdownForm(taskId, op, seconds, note);
                _countdownForm.Show();
            }));
            return true;
        }
        if (message.StartsWith("POWER_CANCEL:", StringComparison.Ordinal))
        {
            if (int.TryParse(message.AsSpan("POWER_CANCEL:".Length), NumberStyles.None, CultureInfo.InvariantCulture, out int taskId))
                this.Invoke(new Action(() =>
                {
                    if (CloseCountdown(taskId)) ShowNotification("Güç işlemi iptal edildi", "Bilgi İşlem planlanan işlemi iptal etti.");
                }));
            return true;
        }
        if (message.StartsWith("POWER_LOCK:", StringComparison.Ordinal))
        {
            if (!int.TryParse(message.AsSpan("POWER_LOCK:".Length), NumberStyles.None, CultureInfo.InvariantCulture, out int taskId)) return true;
            bool locked = false;
            this.Invoke(new Action(() =>
            {
                CloseCountdown(taskId);
                locked = LockWorkStation();
            }));
            if (!locked) TrayLog.Write("Ekran kilitlenemedi (LockWorkStation başarısız).");
            SendToService($"POWER_LOCK_RESULT:{taskId.ToString(CultureInfo.InvariantCulture)}:{(locked ? "1" : "0")}");
            return true;
        }
        if (message.StartsWith("USER_MESSAGE:", StringComparison.Ordinal))
        {
            using JsonDocument? doc = HelpdeskProtocol.Decode(message.Substring("USER_MESSAGE:".Length));
            if (doc == null || !TaskIdOf(doc.RootElement, out int taskId)) return true;
            JsonElement root = doc.RootElement;
            string title = Shorten(HelpdeskProtocol.Text(root, "title"), 80);
            string text = Shorten(HelpdeskProtocol.Text(root, "text"), 1000);
            bool warning = HelpdeskProtocol.Text(root, "style") == "warning";
            bool requiresAck = root.TryGetProperty("requires_ack", out JsonElement ack) && ack.ValueKind == JsonValueKind.True;
            this.Invoke(new Action(() =>
            {
                // Aynı mesaj zaten açıksa (servis tepsi yeniden bağlanınca yeniden gönderir) ikinci pencere açılmaz
                if (_messageForms.TryGetValue(taskId, out UserMessageForm? open) && !open.IsDisposed) return;
                var form = new UserMessageForm(title, text, warning, requiresAck,
                    () => SendToService("USER_MESSAGE_ACK:" + taskId.ToString(CultureInfo.InvariantCulture)));
                form.FormClosed += (_, _) => _messageForms.Remove(taskId);
                _messageForms[taskId] = form;
                form.Show();
            }));
            return true;
        }
        if (message.StartsWith("USER_MESSAGE_CLOSE:", StringComparison.Ordinal))
        {
            if (int.TryParse(message.AsSpan("USER_MESSAGE_CLOSE:".Length), NumberStyles.None, CultureInfo.InvariantCulture, out int taskId))
                this.Invoke(new Action(() =>
                {
                    if (_messageForms.TryGetValue(taskId, out UserMessageForm? form) && !form.IsDisposed) form.Withdraw();
                }));
            return true;
        }
        return false;
    }

    // Açık geri sayımı kapatır (taskId verilirse yalnızca o görevinkini). Dönen: bir pencere kapandı
    private bool CloseCountdown(int? taskId)
    {
        if (_countdownForm == null || _countdownForm.IsDisposed) return false;
        if (taskId != null && _countdownForm.TaskId != taskId) return false;
        _countdownForm.Close();
        _countdownForm = null;
        return true;
    }

    private static bool TaskIdOf(JsonElement root, out int taskId)
    {
        taskId = 0;
        return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("task_id", out JsonElement id)
            && id.ValueKind == JsonValueKind.Number && id.TryGetInt32(out taskId) && taskId > 0;
    }

    // Sınır servisteki gibi Unicode karakteriyle sayılır (emoji 1 karakter); vekil çifti bölünmez
    private static string Shorten(string text, int max)
    {
        int count = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) i++;
            if (++count == max) return text.Substring(0, i + 1);
        }
        return text;
    }
}

// Geri sayım metinleri ("Bilgisayar 60 sn içinde yeniden başlatılacak")
internal static class PowerTexts
{
    public static string Countdown(string op, int seconds) => op switch
    {
        "shutdown" => $"Bilgisayar {seconds} sn içinde kapatılacak",
        "restart" => $"Bilgisayar {seconds} sn içinde yeniden başlatılacak",
        "logoff" => $"Oturumunuz {seconds} sn içinde kapatılacak",
        _ => $"Bilgisayar {seconds} sn içinde kilitlenecek",
    };

    public static string Now(string op) => op switch
    {
        "shutdown" => "Bilgisayar şimdi kapatılıyor…",
        "restart" => "Bilgisayar şimdi yeniden başlatılıyor…",
        "logoff" => "Oturumunuz şimdi kapatılıyor…",
        _ => "Bilgisayar şimdi kilitleniyor…",
    };

    // Kilit dışındaki işlemlerde kaydedilmemiş çalışma kaybolur
    public static string Hint(string op) => op == "lock"
        ? "Kilit açıldığında programlarınız açık kalır."
        : "Açık çalışmalarınızı şimdi kaydedin; kaydedilmemiş değişiklikler kaybolur.";
}

// Üstte duran, odağı çalmayan geri sayım penceresi. Kapatılabilir; işlem yine de yapılır (zamanlayıcı serviste).
internal sealed class PowerCountdownForm : Form
{
    private readonly string _op;
    private readonly Label _headline = new Label { AutoSize = true, Font = new Font("Segoe UI", 14F, FontStyle.Bold), MaximumSize = new Size(460, 0) };
    private readonly Timer _timer = new Timer { Interval = 1000 };
    private int _remaining;
    private bool _finished;

    public PowerCountdownForm(int taskId, string op, int seconds, string note)
    {
        TaskId = taskId;
        _op = op;
        _remaining = seconds;
        Text = "POps - Bilgi İşlem";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Font = new Font("Segoe UI", 10F);

        var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Padding = new Padding(16), WrapContents = false };
        layout.Controls.Add(_headline);
        if (note.Length > 0)
            layout.Controls.Add(new Label { Text = note, AutoSize = true, MaximumSize = new Size(460, 0), Margin = new Padding(3, 10, 3, 3) });
        layout.Controls.Add(new Label { Text = PowerTexts.Hint(op), AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(460, 0), Margin = new Padding(3, 10, 3, 3) });
        Controls.Add(layout);

        ShowRemaining();
        _timer.Tick += (_, _) => Tick();
        FormClosed += (_, _) => _timer.Dispose();
    }

    public int TaskId { get; }

    protected override bool ShowWithoutActivation => true;

    // Üstte durur (TopMost yerine WS_EX_TOPMOST: gösterilirken etkinleşmez)
    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= 0x00000008;
            return cp;
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        SystemSounds.Exclamation.Play();
        _timer.Start();
    }

    private void Tick()
    {
        if (_finished)
        {
            // İşlem başladı; birkaç saniye sonra pencere kapanır (kilitte ekran zaten değişti)
            _timer.Stop();
            Close();
            return;
        }
        _remaining--;
        ShowRemaining();
    }

    private void ShowRemaining()
    {
        if (_remaining > 0)
        {
            _headline.Text = PowerTexts.Countdown(_op, _remaining);
            return;
        }
        _headline.Text = PowerTexts.Now(_op);
        _finished = true;
        _timer.Interval = 10000;
    }
}

// Bilgi İşlem mesajı: başlık, metin ve simge (bilgi/uyarı). Okundu onayı istenen mesaj yalnızca Tamam ile kapanır;
// Enter tuşu düğmeye bağlı değildir ve pencere odağı çalmaz: kullanıcı yazarken mesajı farkında olmadan onaylamasın.
internal sealed class UserMessageForm : Form
{
    private readonly bool _requiresAck;
    private readonly Action _onAck;
    private bool _allowClose;

    public UserMessageForm(string title, string text, bool warning, bool requiresAck, Action onAck)
    {
        _requiresAck = requiresAck;
        _onAck = onAck;
        Text = "POps - Bilgi İşlem mesajı";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ControlBox = !requiresAck;
        ClientSize = new Size(500, 340);
        Font = new Font("Segoe UI", 10F);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 2, RowCount = 3 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var icon = new PictureBox
        {
            Image = (warning ? SystemIcons.Warning : SystemIcons.Information).ToBitmap(),
            SizeMode = PictureBoxSizeMode.AutoSize,
            Margin = new Padding(3, 3, 12, 3),
        };
        layout.Controls.Add(icon, 0, 0);
        layout.Controls.Add(new Label
        {
            Text = title.Length > 0 ? title : "Bilgi İşlem mesajı",
            AutoSize = true,
            Font = new Font("Segoe UI", 12F, FontStyle.Bold),
            MaximumSize = new Size(420, 0),
            Anchor = AnchorStyles.Left,
        }, 1, 0);

        var body = new TextBox
        {
            Text = text.Replace("\n", Environment.NewLine, StringComparison.Ordinal),
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BorderStyle = BorderStyle.None,
            BackColor = SystemColors.Control,
            Dock = DockStyle.Fill,
            Margin = new Padding(3, 10, 3, 3),
            TabStop = false,
        };
        layout.Controls.Add(body, 0, 1);
        layout.SetColumnSpan(body, 2);

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, AutoSize = true };
        var button = new Button { Text = requiresAck ? "Tamam" : "Kapat", AutoSize = true, MinimumSize = new Size(96, 32) };
        button.Click += (_, _) => Acknowledge();
        buttons.Controls.Add(button);
        if (requiresAck)
            buttons.Controls.Add(new Label { Text = "Okuduğunuzu bildirmek için Tamam'a basın.", AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(3, 9, 12, 3) });
        layout.Controls.Add(buttons, 0, 2);
        layout.SetColumnSpan(buttons, 2);
        Controls.Add(layout);

        // Okundu onayı istenen mesaj Alt+F4 ile kapanmaz; Windows kapanırken ve servis geri çekince kapanır
        FormClosing += (_, e) =>
        {
            if (_requiresAck && !_allowClose && e.CloseReason == CloseReason.UserClosing) e.Cancel = true;
        };
        FormClosed += (_, _) => icon.Image?.Dispose();
        (warning ? SystemSounds.Exclamation : SystemSounds.Asterisk).Play();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= 0x00000008;
            return cp;
        }
    }

    // Servis mesajı geri çekti (cancel_task): onay gönderilmeden kapanır
    public void Withdraw()
    {
        _allowClose = true;
        Close();
    }

    private void Acknowledge()
    {
        _allowClose = true;
        if (_requiresAck) _onAck();
        Close();
    }
}
