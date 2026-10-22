using System;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace POpsAgent
{
    // Tepsi borusuna bağlanan istemcinin denetimi: ret nedeni (null: kabul) ve istemcinin süreç kimliği (bilinmiyorsa 0;
    // tepsinin oturumu ve kullanıcısı ondan bulunur)
    public delegate string? PipeClientCheck(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint clientPid);

    // Tepsi borusunun adı ve bağlanan istemcinin denetimi (makine kapısı, bkz. MachineSeams.TrayPipe). Servis kurulu tepsinin
    // borusunu açar (Installed: "POpsTrayPipe", yalnızca kurulum klasöründeki tepsi); kapı verilmeyen kod ve testler kurulu
    // servisle ve gerçek tepsiyle çakışmayan bir ad kullanır (Unique: bağlanan her istemci reddedilir).
    public sealed class TrayPipeOptions
    {
        public const string InstalledName = "POpsTrayPipe";

        // Boruya yalnızca kurulum klasöründeki tepsi bağlanabilir (bkz. PipeClientVerifier)
        private static readonly string TrayExePath = Path.Combine(AppContext.BaseDirectory, "POpsTray.exe");

        public TrayPipeOptions(string name, PipeClientCheck clientCheck)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            ClientCheck = clientCheck ?? throw new ArgumentNullException(nameof(clientCheck));
        }

        public string Name { get; }
        public PipeClientCheck ClientCheck { get; }

        // Servis (yalnızca MachineSeams.Production): kurulu tepsinin borusu, imzası ve yolu doğrulanan tepsi
        internal static TrayPipeOptions Installed() =>
            new TrayPipeOptions(InstalledName, (Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint clientPid) => PipeClientVerifier.Verify(pipe, TrayExePath, out clientPid));

        // Kurulu servisle çakışmayan ad; bağlanan her istemci reddedilir
        public static TrayPipeOptions Unique() => new TrayPipeOptions(string.Concat(InstalledName, "-", Guid.NewGuid().ToString("N").AsSpan(0, 8)), RefuseClient);

        private static string? RefuseClient(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint clientPid)
        {
            clientPid = 0;
            return "makine kapısı verilmedi: tepsi borusuna istemci kabul edilmez";
        }
    }

    public sealed class TrayPipeServer : IDisposable
    {
        private const int MaxPipeMessageBytes = 32 * 1024 * 1024;
        private CancellationTokenSource? _cts;
        private NamedPipeServerStream? _pipeServer;
        private TaskCompletionSource<bool>? _listening;
        public event Action<string> OnMessageReceived = delegate { };
        public event Action<byte[]> OnFrameReceived = delegate { };
        // Vision v2 karesi (POps.Shared.VisionFrame.PipeMagic ile başlar)
        public event Action<byte[]> OnVisionFrame = delegate { };
        // Tepsi bağlantısı koptuğunda (onaylı Vision oturumu da onunla biter)
        public event Action OnDisconnected = delegate { };
        // Doğrulanmış tepsi bağlandı (servis açılışı, tepsinin yeniden başlaması, oturum değişimi)
        public event Action OnConnected = delegate { };
        private readonly object _writeLock = new object();

        // options: borunun adı ve istemci denetimi (servis: kurulu tepsi; testler başka bir ad ve sahte denetim kullanır, gerçek
        // tepsiyle ve kurulu servisle çakışmasın)
        public TrayPipeServer(TrayPipeOptions options)
        {
            Options = options ?? throw new ArgumentNullException(nameof(options));
        }

        public TrayPipeOptions Options { get; }

        public Task Start()
        {
            _cts = new CancellationTokenSource();
            _listening = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = Task.Run(() => ListenPipeAsync(_cts.Token));
            return _listening.Task;
        }

        private string? _lastPipeError;

        // Bağlı tepsinin oturumundaki kullanıcı (bağlantı yoksa null)
        public string? ClientUser { get; private set; }
        // Bağlı tepsinin oturumu (bağlantı yoksa ya da bilinmiyorsa NoSession)
        public uint ClientSession { get; private set; } = UserSessionLauncher.NoSession;

        public bool IsConnected
        {
            get
            {
                try { return _pipeServer?.IsConnected == true; }
                catch (ObjectDisposedException) { return false; }
            }
        }
        public void Stop() { _cts?.Cancel(); _pipeServer?.Dispose(); }

        public void Dispose()
        {
            Stop();
            _cts?.Dispose();
        }

        public void SendCommandToDesktop(string json)
        {
            var pipe = _pipeServer;
            if (pipe == null || !pipe.IsConnected) return;
            try { WriteFrame(pipe, _writeLock, Encoding.UTF8.GetBytes(json)); }
            catch { }
        }

        // Bir mesaj: 4 bayt uzunluk + içerik, tek parça ve kilit altında yazılır. Komut döngüsü, Vision, zamanlayıcılar
        // ve bypass aynı anda yazabilir; kilitsiz iki mesajın parçaları birbirine karışıp tepsinin okumasını bozardı.
        internal static void WriteFrame(Stream stream, object gate, byte[] payload)
        {
            byte[] frame = new byte[4 + payload.Length];
            BitConverter.GetBytes(payload.Length).CopyTo(frame, 0);
            payload.CopyTo(frame, 4);
            lock (gate)
            {
                stream.Write(frame, 0, frame.Length);
                stream.Flush();
            }
        }

        private async Task ListenPipeAsync(CancellationToken token)
        {
            string pipeName = Options.Name;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var ps = new PipeSecurity();
                    // Yalnızca oturum açmış (etkileşimli) kullanıcının tepsi uygulaması bağlanabilir;
                    // ağ ve servis hesapları için Everyone izni kaldırıldı.
                    var interactive = new SecurityIdentifier(WellKnownSidType.InteractiveSid, null);
                    var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
                    // Servis hesabı (LocalSystem; testlerde testi çalıştıran kullanıcı, bkz. SecureStore.SystemSid)
                    var system = SecureStore.SystemSid;
                    ps.AddAccessRule(new PipeAccessRule(system, PipeAccessRights.FullControl, AccessControlType.Allow));
                    ps.AddAccessRule(new PipeAccessRule(admins, PipeAccessRights.FullControl, AccessControlType.Allow));
                    ps.AddAccessRule(new PipeAccessRule(interactive, PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));
                    // Tepsi borunun sahibini denetler (SYSTEM ya da Administrators; bkz. POps.Shared.PipeOwner). Sahip
                    // belirtecin varsayılanına bırakılmaz. ReadWrite, tepsinin sahibi okuması için ReadPermissions'ı içerir.
                    ps.SetOwner(system);

                    _pipeServer = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, ps);
                    _listening?.TrySetResult(true);
                    POpsHelpers.Log("PIPE", $"Bekleniyor: {pipeName}");

                    await _pipeServer.WaitForConnectionAsync(token);
                    string? rejection = Options.ClientCheck(_pipeServer.SafePipeHandle, out uint clientPid);
                    if (rejection != null)
                    {
                        POpsHelpers.Log("PIPE", $"[GÜVENLİK] Tepsi borusuna doğrulanmamış istemci bağlandı, bağlantı kesildi: {rejection}", true);
                        _pipeServer.Disconnect();
                        // Sürekli bağlanıp gerçek tepsiyi dışarıda bırakmaya çalışan istemciyi yavaşlatır
                        await Task.Delay(2000, token);
                        continue;
                    }
                    ClientSession = clientPid == 0 ? UserSessionLauncher.NoSession : UserSessionLauncher.SessionOf((int)clientPid);
                    ClientUser = UserSessionLauncher.SessionUser(ClientSession);
                    _lastPipeError = null;
                    POpsHelpers.Log("PIPE", "🟢 Tepsi bağlandı (doğrulandı).");
                    try { OnConnected?.Invoke(); } catch (Exception ex) { POpsHelpers.Log("PIPE", $"Bağlantı sonrası eşitleme başarısız: {ex.Message}", true); }

                    byte[] lBuf = new byte[4];
                    while (_pipeServer.IsConnected && !token.IsCancellationRequested)
                    {
                        int lRead = 0;
                        while (lRead < 4)
                        {
                            int r = await _pipeServer.ReadAsync(lBuf.AsMemory(lRead, 4 - lRead), token);
                            if (r == 0) break;
                            lRead += r;
                        }
                        if (lRead < 4) break;
                        
                        int dLen = BitConverter.ToInt32(lBuf, 0);
                        // Boru hattına oturum açan her kullanıcı yazabilir: sınırsız ya da negatif uzunluk SYSTEM
                        // servisinin belleğini tüketirdi. En büyük meşru mesaj tepsinin gönderdiği ekran karesidir.
                        if (dLen <= 0 || dLen > MaxPipeMessageBytes)
                        {
                            POpsHelpers.Log("PIPE", $"[GÜVENLİK] Geçersiz mesaj uzunluğu ({dLen}); bağlantı kapatıldı.", true);
                            break;
                        }
                        byte[] d = new byte[dLen];
                        int total = 0;
                        while (total < dLen)
                        {
                            int r = await _pipeServer.ReadAsync(d.AsMemory(total, dLen - total), token);
                            if (r == 0) break;
                            total += r;
                        }
                        if (total == dLen) 
                        {
                            if (VisionFrame.HasPipeMagic(d))
                            {
                                OnVisionFrame?.Invoke(d);
                            }
                            else if (dLen > 2 && d[0] == 0xFF && d[1] == 0xD8)
                            {
                                OnFrameReceived?.Invoke(d);
                            }
                            else
                            {
                                string msg = Encoding.UTF8.GetString(d);
                                OnMessageReceived?.Invoke(msg);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Boru adı başka bir süreçte kaldıkça 3 sn'de bir aynı hata loglanmaz
                    if (ex.Message != _lastPipeError) POpsHelpers.Log("PIPE", $"Hata: {ex.Message}", true);
                    _lastPipeError = ex.Message;
                    await Task.Delay(3000, token);
                }
                finally
                {
                    _pipeServer?.Dispose();
                    ClientUser = null;
                    ClientSession = UserSessionLauncher.NoSession;
                    OnDisconnected?.Invoke();
                }
            }
        }
    }
}
