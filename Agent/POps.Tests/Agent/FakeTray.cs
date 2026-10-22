using System;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace POps.Tests.Agent
{
    // Tepsinin yerine servisin borusuna bağlanan istemci (boru adı ve istemci denetiminin sahtesi testin işidir). Servisin
    // yazdıklarını sürekli okur (okunmayan boruya yazma servisi bekletirdi); tepsi gibi çerçeveli mesaj ve kare gönderir.
    internal sealed class FakeTray : IDisposable
    {
        private readonly NamedPipeClientStream _pipe;
        private readonly List<string> _received = new List<string>();
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);

        // Servisten gelen her mesajda, okuma döngüsünün dışında çağrılır (ör. CAPTURE_SNAPSHOT'a kareyle yanıt)
        public Func<string, Task> OnMessage { get; set; } = _ => Task.CompletedTask;

        private FakeTray(NamedPipeClientStream pipe) => _pipe = pipe;

        public static async Task<FakeTray> ConnectAsync(string pipeName)
        {
            var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(5000);
            var tray = new FakeTray(pipe);
            _ = Task.Run(tray.ReadLoopAsync);
            return tray;
        }

        // Servisin tepsiye şimdiye kadar yazdığı mesajlar, sırayla
        public List<string> Received { get { lock (_received) return new List<string>(_received); } }

        private async Task ReadLoopAsync()
        {
            try
            {
                byte[] length = new byte[4];
                while (await ReadExactlyAsync(length))
                {
                    byte[] data = new byte[BitConverter.ToInt32(length, 0)];
                    if (!await ReadExactlyAsync(data)) return;
                    string message = Encoding.UTF8.GetString(data);
                    lock (_received) _received.Add(message);
                    // Okuma beklemez: yanıt yazılırken servis de yazıyor olabilir (borunun tamponu yok; ikisi birden yazarsa
                    // ve kimse okumazsa ikisi de bekler)
                    Func<string, Task> handler = OnMessage;
                    _ = Task.Run(() => handler(message));
                }
            }
            catch (Exception) { }
        }

        private async Task<bool> ReadExactlyAsync(byte[] buffer)
        {
            int read = 0;
            while (read < buffer.Length)
            {
                int r = await _pipe.ReadAsync(buffer, read, buffer.Length - read);
                if (r == 0) return false;
                read += r;
            }
            return true;
        }

        public Task SendAsync(string message) => SendFrameAsync(Encoding.UTF8.GetBytes(message));

        public async Task SendFrameAsync(byte[] data)
        {
            byte[] frame = new byte[4 + data.Length];
            BitConverter.GetBytes(data.Length).CopyTo(frame, 0);
            data.CopyTo(frame, 4);
            await _writeLock.WaitAsync();
            try
            {
                await _pipe.WriteAsync(frame, 0, frame.Length);
                await _pipe.FlushAsync();
            }
            finally { _writeLock.Release(); }
        }

        // Koşul süre içinde sağlandı mı (50 ms'de bir bakılır)
        public static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
        {
            for (int waited = 0; waited < timeoutMs && !condition(); waited += 50) await Task.Delay(50);
            return condition();
        }

        public void Dispose()
        {
            _pipe.Dispose();
            _writeLock.Dispose();
        }
    }
}
