using System.Threading;

namespace POpsTray;

static class Program
{
    private static Mutex _mutex = null;

    /// <summary>
    ///  The main entry point for the application.
    /// </summary>
    [STAThread]
    static void Main(string[] args)
    {
        const string appName = @"Global\POpsTrayApp_SingleInstance";
        bool createdNew;

        _mutex = new Mutex(true, appName, out createdNew);

        if (!createdNew)
        {
            // Zaten bir kopya çalışıyor, yeni açılanı kapat.
            return;
        }

        bool isStealth = args.Contains("--stealth");

        try
        {
            // Otomatik başlatma kaydı
            using (Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true))
            {
                if (key != null)
                {
                    key.SetValue("POpsTrayApp", Application.ExecutablePath);
                }
            }
        }
        catch { }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(isStealth));

        GC.KeepAlive(_mutex);
    }    
}