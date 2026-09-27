using System;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

#nullable disable

namespace POpsAgent
{
    // Servis açılışında ve sonra 30 sn'de bir: etkin konsol oturumunda kullanıcı varsa ve watchdog/tepsi hiçbir
    // oturumda çalışmıyorsa onları o oturumda başlatır (bkz. POps.Shared.UserSessionLauncher). Kurulumdan ve
    // güncellemeden sonra tepsi böylece oturumu kapatıp açmadan en geç ~30 sn'de gelir; karantinada tepsi yoksa kilit
    // ekranı da onunla gelir (tepsi bağlanınca servis kilidi yeniden gösterir). Güncelleme sürerken (update.lock)
    // dokunulmaz: updater onları bilerek kapatır ve iş bitince kendisi başlatır.
    [SupportedOSPlatform("windows")]
    public sealed class UserSessionApps
    {
        public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

        private readonly string _installDir;
        private uint _session = UserSessionLauncher.NoSession;
        private DateTime _signedInSinceUtc;
        private string _lastProblem;
        // Art arda başlatma sayısı (program görülünce sıfırlanır): hemen kapanan program 30 sn'de bir loglanmasın
        private readonly System.Collections.Generic.Dictionary<string, int> _starts = new System.Collections.Generic.Dictionary<string, int>();

        public UserSessionApps(string installDir = null) => _installDir = installDir ?? AppContext.BaseDirectory;

        public async Task RunAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                EnsureOnce();
                try { await Task.Delay(Interval, token); }
                catch (TaskCanceledException) { return; }
            }
        }

        public void EnsureOnce()
        {
            try
            {
                uint session = UserSessionLauncher.ActiveConsoleSession();
                if (!UserSessionLauncher.HasSignedInUser(session))
                {
                    _session = UserSessionLauncher.NoSession;
                    return;
                }
                if (session != _session)
                {
                    _session = session;
                    _signedInSinceUtc = DateTime.UtcNow;
                }

                bool watchdogRunning = UserSessionLauncher.IsRunning(Path.Combine(_installDir, "POpsWatchdog.exe"));
                bool trayRunning = UserSessionLauncher.IsRunning(Path.Combine(_installDir, "POpsTray.exe"));
                if (watchdogRunning) _starts.Remove("POpsWatchdog.exe");
                if (trayRunning) _starts.Remove("POpsTray.exe");
                var (watchdog, tray) = UserAppsPolicy.WhatToStart(
                    userSignedIn: true,
                    // update.lock yalnızca ajanın başlattığı güncellemede var; elle/GPO ile MSI kurulumu için Windows Installer'a da bakılır
                    updateInProgress: AgentUpdate.IsLockFresh() || UserSessionLauncher.WindowsInstallerBusy(),
                    watchdogRunning: watchdogRunning,
                    trayRunning: trayRunning,
                    shellReady: UserSessionLauncher.ShellReady(session),
                    signedInFor: DateTime.UtcNow - _signedInSinceUtc);
                if (watchdog) Start(session, "POpsWatchdog.exe");
                if (tray) Start(session, "POpsTray.exe");
            }
            catch (Exception ex) { Problem($"Tepsi/watchdog denetlenemedi: {ex.Message}"); }
        }

        private void Start(uint session, string exeName)
        {
            if (UserSessionLauncher.TryStart(session, Path.Combine(_installDir, exeName), out int pid, out string error))
            {
                _lastProblem = null;
                int count = _starts.TryGetValue(exeName, out int n) ? n + 1 : 1;
                _starts[exeName] = count;
                if (count == 1) POpsHelpers.Log("AGENT", $"{exeName} kullanıcı oturumunda başlatıldı (oturum {session}, PID {pid}).");
                else if (count == 2) POpsHelpers.Log("AGENT", $"{exeName} başlatıldıktan sonra hemen kapanıyor; yeniden denenecek ama artık loglanmayacak.", true);
            }
            else Problem($"{exeName} kullanıcı oturumunda başlatılamadı: {error}");
        }

        // Aynı hata 30 sn'de bir yeniden loglanmaz
        private void Problem(string message)
        {
            if (message == _lastProblem) return;
            _lastProblem = message;
            POpsHelpers.Log("AGENT", message, true);
        }
    }
}
