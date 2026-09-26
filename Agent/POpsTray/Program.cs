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
        // Otomatik başlatmanın tek sahibi MSI'dır (HKLM\...\Run "POpsTray"). Eski sürümlerin kullanıcı başına
        // yazdığı kayıt, eski tepsiyi de açabildiği için temizlenir. Tek kopya kilidinden önce yapılır: kilidi
        // eski tepsi tutsa bile kayıt silinir. MSI'ın Active Setup kaydı her kullanıcının ilk oturumunda
        // tepsiyi --cleanup-autostart ile yalnızca bunun için çalıştırır.
        RemoveLegacyAutostart();
        if (args.Contains("--cleanup-autostart", StringComparer.OrdinalIgnoreCase)) return;

        const string appName = @"Global\POpsTrayApp_SingleInstance";
        bool createdNew;

        _mutex = new Mutex(true, appName, out createdNew);

        if (!createdNew)
        {
            // Zaten bir kopya çalışıyor, yeni açılanı kapat.
            return;
        }

        // Gizli (--stealth) mod kaldırıldı: tepsi simgesi ve bildirimler her zaman görünür.

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());

        GC.KeepAlive(_mutex);
    }

    // Yalnızca kullanıcının kendi (HKCU) kayıtları; MSI'ın HKLM kaydına dokunulmaz
    private static void RemoveLegacyAutostart()
    {
        try
        {
            using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
            key?.DeleteValue("POpsTrayApp", false);
            key?.DeleteValue("POpsTray", false);
        }
        catch { }
    }
}