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

        // Gizli (--stealth) mod kaldırıldı: tepsi simgesi ve bildirimler her zaman görünür.

        // Otomatik başlatmanın tek sahibi MSI'dır (HKLM\...\Run "POpsTray"). Eski sürümlerin kullanıcı
        // başına yazdığı kayıt, silinmiş eski kurulum klasörünü gösterebileceği için temizlenir.
        try
        {
            using (Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true))
            {
                key?.DeleteValue("POpsTrayApp", false);
            }
        }
        catch { }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());

        GC.KeepAlive(_mutex);
    }    
}