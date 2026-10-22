using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

#nullable disable

namespace POpsUpdater
{
    // Bir .msi paketini salt okunur açıp birkaç değerini okur (kurulum yapmaz). Geri dönüşten önce önceki
    // paketin gerçekten POps Agent paketi olduğunu ve geri dönüşü (POPS_ROLLBACK) desteklediğini doğrular.
    [SupportedOSPlatform("windows")]
    sealed class MsiPackage
    {
        public string UpgradeCode { get; private set; }
        public string ProductCode { get; private set; }
        public string ProductVersion { get; private set; }
        // Package.wxs: daha yeni sürüm kuruluyken kuruluma yalnızca POPS_ROLLBACK=1 ile izin veren Upgrade satırı
        public bool SupportsRollback { get; private set; }

        public static MsiPackage TryRead(string path, out string error)
        {
            error = null;
            if (MsiOpenDatabase(path, IntPtr.Zero, out IntPtr db) != 0)
            {
                error = "MSI veritabanı açılamadı";
                return null;
            }
            try
            {
                return new MsiPackage
                {
                    UpgradeCode = Query(db, "SELECT `Value` FROM `Property` WHERE `Property` = 'UpgradeCode'"),
                    ProductCode = Query(db, "SELECT `Value` FROM `Property` WHERE `Property` = 'ProductCode'"),
                    ProductVersion = Query(db, "SELECT `Value` FROM `Property` WHERE `Property` = 'ProductVersion'"),
                    SupportsRollback = Query(db, "SELECT `ActionProperty` FROM `Upgrade` WHERE `ActionProperty` = 'POPS_NEWER_DETECTED'") != null,
                };
            }
            finally { MsiCloseHandle(db); }
        }

        // İlk satırın ilk alanı; satır ya da tablo yoksa null
        static string Query(IntPtr db, string sql)
        {
            if (MsiDatabaseOpenView(db, sql, out IntPtr view) != 0) return null;
            try
            {
                if (MsiViewExecute(view, IntPtr.Zero) != 0 || MsiViewFetch(view, out IntPtr record) != 0) return null;
                try
                {
                    uint size = 256;
                    var buffer = new StringBuilder((int)size);
                    uint rc = MsiRecordGetString(record, 1, buffer, ref size);
                    if (rc == ErrorMoreData)
                    {
                        buffer = new StringBuilder((int)++size);
                        rc = MsiRecordGetString(record, 1, buffer, ref size);
                    }
                    return rc == 0 ? buffer.ToString() : null;
                }
                finally { MsiCloseHandle(record); }
            }
            finally
            {
                MsiViewClose(view);
                MsiCloseHandle(view);
            }
        }

        // Bu ProductCode bu makinede kurulu mu (INSTALLSTATE_DEFAULT)
        public static bool IsInstalled(string productCode) =>
            !string.IsNullOrEmpty(productCode) && MsiQueryProductState(productCode) == InstallStateDefault;

        const int InstallStateDefault = 5;
        const uint ErrorMoreData = 234;

        [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiQueryProductStateW")]
        static extern int MsiQueryProductState(string productCode);

        // szPersist = MSIDBOPEN_READONLY (0)
        [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiOpenDatabaseW")]
        static extern uint MsiOpenDatabase(string databasePath, IntPtr persist, out IntPtr database);

        [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiDatabaseOpenViewW")]
        static extern uint MsiDatabaseOpenView(IntPtr database, string query, out IntPtr view);

        [DllImport("msi.dll")]
        static extern uint MsiViewExecute(IntPtr view, IntPtr record);

        [DllImport("msi.dll")]
        static extern uint MsiViewFetch(IntPtr view, out IntPtr record);

        [DllImport("msi.dll")]
        static extern uint MsiViewClose(IntPtr view);

        [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiRecordGetStringW")]
        static extern uint MsiRecordGetString(IntPtr record, uint field, StringBuilder value, ref uint size);

        [DllImport("msi.dll")]
        static extern uint MsiCloseHandle(IntPtr handle);
    }
}
