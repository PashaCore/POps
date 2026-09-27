using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;

namespace POps.Shared
{
    // Tepsi ve watchdog kullanıcının oturumunda çalışır; servis (SYSTEM, oturum 0) ve updater onları etkin konsol
    // oturumunda, oturumdaki kullanıcı olarak başlatır: WTSGetActiveConsoleSessionId + WTSQueryUserToken +
    // CreateProcessAsUser. Eskiden "schtasks /ru BUILTIN\Users /it" kullanılıyordu: grup adına kayıtlı görevin elle
    // çalıştırılması etkin oturumu hedeflemiyor, "BUILTIN\Users" bazı Windows dillerinde çözülmüyor ve bütün çıktı
    // nul'a gittiği için başarısızlık görünmüyordu. Sahada tepsi kurulumdan ve güncellemeden sonra ancak bir sonraki
    // oturum açılışında (HKLM\...\Run) geliyordu; karantinada tepsi yoksa kilit ekranı da gösterilemez.
    // WTSQueryUserToken yalnızca SYSTEM'de (SeTcbPrivilege) çalışır.
    [SupportedOSPlatform("windows")]
    public static class UserSessionLauncher
    {
        public const uint NoSession = 0xFFFFFFFF;

        // Etkin konsol oturumu (fiziksel ekran); yoksa NoSession
        public static uint ActiveConsoleSession() => WTSGetActiveConsoleSessionId();

        // Oturumda kullanıcı var mı (kilit ekranı değil, oturum açılmış). SYSTEM dışında hep false.
        public static bool HasSignedInUser(uint sessionId)
        {
            if (sessionId == NoSession) return false;
            if (!WTSQueryUserToken(sessionId, out IntPtr token)) return false;
            CloseHandle(token);
            return true;
        }

        // Kurulum klasöründeki program makinede herhangi bir oturumda çalışıyor mu. Yalnızca ada bakılmaz: İndirilenler'e
        // kopyalanıp POpsTray.exe adı verilen başka bir program, servisin gerçek tepsiyi (karantinada kilit ekranını)
        // yeniden başlatmasını engelleyemesin. Yolu okunamayan süreç sayılmaz (SYSTEM her sürecin yolunu okuyabilir;
        // kullanıcı yalnızca kendi süreçlerininkini).
        public static bool IsRunning(string expectedPath, int excludeProcessId = 0)
        {
            string name = Path.GetFileNameWithoutExtension(expectedPath);
            Process[] found = Process.GetProcessesByName(name);
            try
            {
                return found.Any(p =>
                {
                    if (p.Id == excludeProcessId) return false;
                    string image = ImagePath(p.Id);
                    return image != null && SamePath(image, expectedPath);
                });
            }
            finally
            {
                foreach (Process p in found) p.Dispose();
            }
        }

        public static string ImagePath(int processId)
        {
            IntPtr process = OpenProcess(ProcessQueryLimitedInformation, false, (uint)processId);
            if (process == IntPtr.Zero) return null;
            try
            {
                var buffer = new StringBuilder(1024);
                int size = buffer.Capacity;
                return QueryFullProcessImageName(process, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
            }
            finally { CloseHandle(process); }
        }

        public static bool SamePath(string a, string b)
        {
            try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        // Windows Installer bir işlem yürütüyorsa (elle ya da GPO ile MSI kurulumu/yükseltmesi update.lock yazmaz)
        // tepsi ve watchdog başlatılmaz: kurulumun kapattığı dosyalarla yarışılmasın
        public static bool WindowsInstallerBusy(string mutexName = @"Global\_MSIExecute")
        {
            try
            {
                if (!Mutex.TryOpenExisting(mutexName, out Mutex mutex)) return false;
                mutex.Dispose();
                return true;
            }
            catch (UnauthorizedAccessException) { return true; }   // var ama açılamıyor: yine de meşgul
            catch { return false; }
        }

        // Sürecin oturumu ve o oturumun kullanıcısı (yardım masası talebinin sahibi: isteği yapan tepsi). SYSTEM'de çalışır.
        public static uint SessionOf(int processId) => ProcessIdToSessionId((uint)processId, out uint session) ? session : NoSession;

        public static string SessionUser(uint sessionId)
        {
            if (sessionId == NoSession) return null;
            if (!WTSQuerySessionInformationW(IntPtr.Zero, sessionId, WTSUserName, out IntPtr buffer, out _)) return null;
            try
            {
                string user = Marshal.PtrToStringUni(buffer)?.Trim();
                return string.IsNullOrEmpty(user) ? null : user;
            }
            finally { WTSFreeMemory(buffer); }
        }

        // Kabuk (explorer) oturumda açılmış mı: tepsi simgesi görev çubuğu hazır olmadan eklenmesin
        public static bool ShellReady(uint sessionId)
        {
            Process[] shells = Process.GetProcessesByName("explorer");
            bool ready = shells.Any(p => { try { return p.SessionId == (int)sessionId; } catch { return false; } });
            foreach (Process p in shells) p.Dispose();
            return ready;
        }

        // exePath'i oturumdaki kullanıcı olarak başlatır. Dönen: başarılı mı; error: nedeni (loglamak için)
        public static bool TryStart(uint sessionId, string exePath, out int processId, out string error)
        {
            processId = 0;
            error = null;
            if (!File.Exists(exePath)) { error = $"{exePath} yok"; return false; }
            if (sessionId == NoSession) { error = "etkin konsol oturumu yok"; return false; }

            IntPtr userToken = IntPtr.Zero, primary = IntPtr.Zero, environment = IntPtr.Zero;
            try
            {
                if (!WTSQueryUserToken(sessionId, out userToken)) { error = $"oturum {sessionId} kullanıcı belirteci alınamadı (Win32 {Marshal.GetLastWin32Error()})"; return false; }
                if (!DuplicateTokenEx(userToken, MaximumAllowed, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out primary)) { error = $"belirteç kopyalanamadı (Win32 {Marshal.GetLastWin32Error()})"; return false; }
                // Kullanıcının ortamı kurulamazsa başlatılmaz: yoksa süreç SYSTEM'in (servisin) ortamını alırdı
                if (!CreateEnvironmentBlock(out environment, primary, false))
                {
                    environment = IntPtr.Zero;
                    error = $"kullanıcı ortamı kurulamadı (Win32 {Marshal.GetLastWin32Error()})";
                    return false;
                }

                var startup = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>(), lpDesktop = @"winsta0\default" };
                var commandLine = new StringBuilder("\"" + exePath + "\"");
                uint flags = CreateUnicodeEnvironment | CreateNoWindow;
                if (!CreateProcessAsUser(primary, exePath, commandLine, IntPtr.Zero, IntPtr.Zero, false, flags, environment, Path.GetDirectoryName(exePath), ref startup, out ProcessInformation info))
                {
                    error = $"CreateProcessAsUser başarısız (Win32 {Marshal.GetLastWin32Error()})";
                    return false;
                }
                processId = info.dwProcessId;
                CloseHandle(info.hThread);
                CloseHandle(info.hProcess);
                return true;
            }
            finally
            {
                if (environment != IntPtr.Zero) DestroyEnvironmentBlock(environment);
                if (primary != IntPtr.Zero) CloseHandle(primary);
                if (userToken != IntPtr.Zero) CloseHandle(userToken);
            }
        }

        private const uint MaximumAllowed = 0x02000000;
        private const int SecurityImpersonation = 2;
        private const int TokenPrimary = 1;
        private const uint CreateUnicodeEnvironment = 0x00000400;
        private const uint CreateNoWindow = 0x08000000;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StartupInfo
        {
            public int cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInformation
        {
            public IntPtr hProcess, hThread;
            public int dwProcessId, dwThreadId;
        }

        [DllImport("kernel32.dll")]
        private static extern uint WTSGetActiveConsoleSessionId();

        [DllImport("wtsapi32.dll", SetLastError = true)]
        private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool DuplicateTokenEx(IntPtr existingToken, uint desiredAccess, IntPtr tokenAttributes, int impersonationLevel, int tokenType, out IntPtr newToken);

        [DllImport("userenv.dll", SetLastError = true)]
        private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);

        [DllImport("userenv.dll", SetLastError = true)]
        private static extern bool DestroyEnvironmentBlock(IntPtr environment);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcessAsUser(IntPtr token, string applicationName, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
            bool inheritHandles, uint creationFlags, IntPtr environment, string currentDirectory, ref StartupInfo startupInfo, out ProcessInformation processInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        private const uint ProcessQueryLimitedInformation = 0x1000;
        private const int WTSUserName = 5;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint processId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

        [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool WTSQuerySessionInformationW(IntPtr server, uint sessionId, int infoClass, out IntPtr buffer, out uint bytes);

        [DllImport("wtsapi32.dll")]
        private static extern void WTSFreeMemory(IntPtr memory);
    }

    // Servisin kayıt defterindeki ImagePath'inden exe yolu. Tırnaklı ("C:\Program Files\POps\POpsAgent.exe" -x)
    // ya da tırnaksız ve boşluklu (C:\Program Files\POps\POpsAgent.exe) olabilir: ".exe"ye kadar okunur; boşlukta
    // kesmek "C:\Program" verirdi.
    public static class ServiceImagePath
    {
        public static string ExecutablePath(string imagePath)
        {
            if (string.IsNullOrWhiteSpace(imagePath)) return null;
            string value = imagePath.Trim();
            if (value.StartsWith("\""))
            {
                int end = value.IndexOf('"', 1);
                return end > 1 ? value.Substring(1, end - 1) : null;
            }
            int exe = value.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            return exe > 0 ? value.Substring(0, exe + 4) : value.Split(' ')[0];
        }
    }

    // Hangi kullanıcı uygulaması başlatılmalı (saf karar; testlerde denenir)
    public static class UserAppsPolicy
    {
        // Oturum açıldıktan sonra kabuk (explorer) hiç görünmezse (özel kabuk) tepsi yine de bu kadar sonra başlatılır
        public static readonly TimeSpan ShellWait = TimeSpan.FromSeconds(60);

        public static (bool Watchdog, bool Tray) WhatToStart(bool userSignedIn, bool updateInProgress, bool watchdogRunning, bool trayRunning, bool shellReady, TimeSpan signedInFor)
        {
            // Güncelleme sürerken updater tepsiyi ve watchdog'u bilerek kapatır; msiexec ile yarışılmaz
            if (!userSignedIn || updateInProgress) return (false, false);
            bool tray = !trayRunning && (shellReady || signedInFor >= ShellWait);
            return (!watchdogRunning, tray);
        }
    }
}
