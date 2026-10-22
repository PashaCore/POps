namespace POpsTray;

// Tepsinin kendi logu: kullanıcının %LOCALAPPDATA%\POps\Logs\TrayLog.txt dosyası, 1 MB'ta döner (bir eski kopya).
// Eskiden C:\POpsLogs\TrayLog.txt'ye gelen her mesaj olduğu gibi yazılıyordu: yöneticinin uzaktan bastığı tuşlar
// (parolalar dahil) herkesin okuyabildiği bir dosyada düz metin kalıyordu. Artık mesaj içeriği yazılmaz; yalnızca
// mesaj türü ve hatalar loglanır.
internal static class TrayLog
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly object Gate = new();
    private static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "POps", "Logs");
    private static readonly string FilePath = Path.Combine(Dir, "TrayLog.txt");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                var file = new FileInfo(FilePath);
                if (file.Exists && file.Length > MaxBytes) File.Move(FilePath, Path.Combine(Dir, "TrayLog.1.txt"), true);
                File.AppendAllText(FilePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
            }
        }
        catch { }
    }

    // Servisten gelen mesajın yalnızca türü: komut adı ya da JSON'daki type/action. Uzaktan girdi (tuş, fare)
    // hiç loglanmaz.
    public static string Describe(string message)
    {
        if (string.IsNullOrEmpty(message)) return "(boş)";
        if (!message.TrimStart().StartsWith('{'))
        {
            int colon = message.IndexOf(':');
            return colon > 0 ? message.Substring(0, colon) : (message.Length > 40 ? message.Substring(0, 40) : message);
        }
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(message);
            var root = doc.RootElement;
            string? type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            string? action = root.TryGetProperty("action", out var a) ? a.GetString() : null;
            return $"type={type ?? "-"} action={action ?? "-"}";
        }
        catch { return "(çözümlenemeyen JSON)"; }
    }
}
