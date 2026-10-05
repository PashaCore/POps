using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using POpsAgent;
using Xunit;

namespace POps.Tests.Agent
{
    // Tepsi borusu sunucu bağlantısından bağımsızdır: sunucuya ulaşılamazken çevrimdışı bypass kodu servise ulaşmalı.
    // Gerçek boru açılır (testlere özel ad); tepsinin yerine istemci doğrulamasını geçen sahte bir istemci bağlanır.
    public class TrayPipeLifetimeTests : TestBase, IDisposable
    {
        private const string HwId = "HW-PIPE00000001";
        private const string FleetSecret = "pipe-test-secret";
        private readonly List<string> _tray = new List<string>();
        private readonly Worker _worker;

        public TrayPipeLifetimeTests()
        {
            SecureStore.Dir = TestEnvironment.NewDir("pipe-secure");
            AgentUpdate.DataDir = TestEnvironment.NewDir("pipe-data");
            TrayPipeServer.PipeName = "POpsTrayPipe-test-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            TrayPipeServer.ClientCheckOverride = _ => null;
            SecureStore.WriteProtected(SecureStore.PathOf(AgentCredentials.BypassSecretFileName), FleetSecret);
            _worker = new Worker(NullLogger<Worker>.Instance)
            {
                HwId = HwId,
                SendOverride = _ => Task.FromResult(false),   // sunucuya ulaşılamıyor
            };
            _worker.Quarantine = new QuarantineControl(
                message => { lock (_tray) _tray.Add(message); },
                () => { File.WriteAllText(NetworkIsolation.StatePath, "{}"); return Task.FromResult(true); },
                () => { File.Delete(NetworkIsolation.StatePath); return Task.FromResult(true); });
        }

        public void Dispose()
        {
            _worker.TrayPipe?.Stop();
            _worker.Dispose();
            TrayPipeServer.PipeName = "POpsTrayPipe";
            TrayPipeServer.ClientCheckOverride = null;
            SecureStore.Dir = TestEnvironment.DefaultSecureDir;
            AgentUpdate.DataDir = TestEnvironment.DefaultDataDir;
        }

        // Filo anahtarlı (eski) çevrimdışı kod: SHA-256(hw_id + anahtar + yerel tarih), ilk 6 hane
        private static string TodaysCode() =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(HwId + FleetSecret + DateTime.Now.ToString("yyyy-MM-dd")))).Substring(0, 6);

        // Gerçek tepsi gibi servisin yazdıklarını sürekli okur (okunmayan boruya yazma servisi bekletirdi)
        private readonly List<string> _fromService = new List<string>();

        private async Task<NamedPipeClientStream> ConnectTrayAsync()
        {
            var client = new NamedPipeClientStream(".", TrayPipeServer.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(5000);
            _ = Task.Run(async () =>
            {
                try
                {
                    byte[] length = new byte[4];
                    while (await client.ReadAsync(length, 0, 4) == 4)
                    {
                        byte[] data = new byte[BitConverter.ToInt32(length, 0)];
                        int read = 0;
                        while (read < data.Length)
                        {
                            int r = await client.ReadAsync(data, read, data.Length - read);
                            if (r == 0) return;
                            read += r;
                        }
                        lock (_fromService) _fromService.Add(Encoding.UTF8.GetString(data));
                    }
                }
                catch (Exception) { }
            });
            return client;
        }

        private static async Task SendAsync(Stream pipe, string message)
        {
            byte[] data = Encoding.UTF8.GetBytes(message);
            await pipe.WriteAsync(BitConverter.GetBytes(data.Length), 0, 4);
            await pipe.WriteAsync(data, 0, data.Length);
            await pipe.FlushAsync();
        }

        private async Task<bool> WaitUntilAsync(Func<bool> condition)
        {
            for (int i = 0; i < 100 && !condition(); i++) await Task.Delay(50);
            return condition();
        }

        [Fact]
        public async Task OfflineBypassCode_ReachesTheService_AfterTheServerConnectionDropped()
        {
            _worker.EnsureTrayPipeServer();
            using NamedPipeClientStream tray = await ConnectTrayAsync();
            Assert.True(await _worker.Quarantine.LockdownAsync("test"));
            Assert.True(_worker.Quarantine.IsLocked);

            // Sunucu bağlantısı koptu (ağ kesildi): 0.1.22'ye kadar boru burada kapanıyordu
            await _worker.OnCommandConnectionLostAsync();
            Assert.True(tray.IsConnected);

            await SendAsync(tray, "UNLOCK_BYPASS:" + TodaysCode());
            Assert.True(await WaitUntilAsync(() => !_worker.Quarantine.IsLocked), "kod servise ulaşmadı; karantina sürüyor");
            lock (_tray) Assert.Contains("BYPASS_SUCCESS", _tray);
        }

        [Fact]
        public async Task Pipe_IsOpenedOnce_AndAcceptsTheTrayBetweenConnectionAttempts()
        {
            _worker.EnsureTrayPipeServer();
            TrayPipeServer pipe = _worker.TrayPipe;
            await _worker.OnCommandConnectionLostAsync();
            _worker.EnsureTrayPipeServer();   // bir sonraki bağlanma denemesi
            Assert.Same(pipe, _worker.TrayPipe);

            // Bekleme sırasında (sunucuya bağlanılmıyorken) tepsi bağlanabilir
            using NamedPipeClientStream tray = await ConnectTrayAsync();
            Assert.True(await WaitUntilAsync(() => pipe.IsConnected));
        }
    }

    // L12: tepsiye giden mesajlar birden çok thread'den yazılsa da çerçeveler karışmaz
    public class PipeFrameTests : TestBase
    {
        // Her baytı ayrı yazan ve araya diğer thread'leri sokan akış: kilit yoksa çerçeveler iç içe geçer
        private sealed class TricklingStream : Stream
        {
            private readonly List<byte> _data = new List<byte>();
            public byte[] Data { get { lock (_data) return _data.ToArray(); } }
            public override void Write(byte[] buffer, int offset, int count)
            {
                for (int i = 0; i < count; i++)
                {
                    lock (_data) _data.Add(buffer[offset + i]);
                    if (i % 3 == 0) Thread.Yield();
                }
            }
            public override void Flush() { }
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }

        [Fact]
        public void ConcurrentWrites_KeepFramesIntact()
        {
            var stream = new TricklingStream();
            var gate = new object();
            const int threads = 8, perThread = 60;
            Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, t =>
            {
                for (int i = 0; i < perThread; i++)
                    TrayPipeServer.WriteFrame(stream, gate, Encoding.UTF8.GetBytes($"{{\"action\":\"t{t}\",\"n\":{i},\"pad\":\"{new string('x', 20 + t)}\"}}"));
            });

            byte[] data = stream.Data;
            var seen = new HashSet<string>();
            int pos = 0;
            while (pos < data.Length)
            {
                int length = BitConverter.ToInt32(data, pos);
                Assert.InRange(length, 1, 200);
                string message = Encoding.UTF8.GetString(data, pos + 4, length);
                Assert.Matches("^\\{\"action\":\"t\\d\",\"n\":\\d+,\"pad\":\"x+\"\\}$", message);
                seen.Add(message);
                pos += 4 + length;
            }
            Assert.Equal(threads * perThread, seen.Count);
        }
    }
}
