using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

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

        // Süreç makinede herhangi bir oturumda çalışıyor mu (tepsinin tek kopya kilidi makine geneli)
        public static bool IsRunning(string processName)
        {
            Process[] found = Process.GetProcessesByName(processName);
            foreach (Process p in found) p.Dispose();
            return found.Length > 0;
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
                if (!CreateEnvironmentBlock(out environment, primary, false)) environment = IntPtr.Zero;

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
