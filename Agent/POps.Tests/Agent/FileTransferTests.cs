using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using POpsAgent;
using Xunit;

namespace POps.Tests.Agent
{
    // Dosya aktarımı: ad temizleme, yalnızca kendi sunucusu, izinli hedefler, çekmede profil ve güvenli klasör kuralları
    public class FileTransferTests : TestBase, IDisposable
    {
        private const string Server = "https://pops.example";
        private const string Secret = "files-secret-0123456789abcdefghijklm";
        private readonly HttpClient _client = AgentHttp.Client;
        private readonly string _desktop = TestEnvironment.NewDir("public-desktop");
        private readonly string _profiles = TestEnvironment.NewDir("users");
        private readonly byte[] _content = RandomNumberGenerator.GetBytes(5000);
        private HttpRequestMessage _seen;
        private byte[] _uploaded;

        public FileTransferTests()
        {
            SecureStore.Dir = TestEnvironment.NewDir("files-secure");
            AgentUpdate.DataDir = TestEnvironment.NewDir("files-data");
            AgentCapabilities.Load();
            AgentCredentials.SaveSecret(Secret, "HW-FILES");
            FileTransfer.PublicDesktopOverride = () => _desktop;
            Directory.CreateDirectory(Path.Combine(_profiles, "ali"));
            Directory.CreateDirectory(Path.Combine(_profiles, "ayse"));
            Directory.CreateDirectory(Path.Combine(_profiles, "Public"));
            FileTransfer.ProfileInfo = () => (_profiles, Path.Combine(_profiles, "ali"));
            AgentHttp.Client = new HttpClient(new Handler(this));
        }

        public void Dispose()
        {
            AgentHttp.Client = _client;
            FileTransfer.PublicDesktopOverride = null;
            FileTransfer.ProfileInfo = null;
            SecureStore.Dir = TestEnvironment.DefaultSecureDir;
            AgentUpdate.DataDir = TestEnvironment.DefaultDataDir;
            AgentCapabilities.Load();
        }

        private sealed class Handler : HttpMessageHandler
        {
            private readonly FileTransferTests _t;
            public Handler(FileTransferTests t) { _t = t; }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                _t._seen = request;
                if (request.Method == HttpMethod.Post)
                {
                    _t._uploaded = await request.Content.ReadAsByteArrayAsync(cancellationToken);
                    return new HttpResponseMessage(HttpStatusCode.OK);
                }
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_t._content) };
            }
        }

        private string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

        private JsonElement Push(string name = "ödev.pdf", string dest = "inbox", string sha = null, long? size = null, bool allowExec = false, string url = "/api/files/7/download?t=abc") =>
            Json(new { action = "file_push", transfer_id = "t-7", name, size = size ?? _content.Length, sha256 = sha ?? Sha(_content), url, dest, reason = "ders notu", allow_exec = allowExec });

        [Theory]
        [InlineData("rapor.pdf", "rapor.pdf")]
        [InlineData("  Ödev 1.docx. ", "Ödev 1.docx")]
        [InlineData("a/b.txt", null)]
        [InlineData("..\\x.txt", null)]
        [InlineData("dosya.txt:gizli", null)]
        [InlineData("CON", null)]
        [InlineData("con.txt", null)]
        [InlineData("COM1.log", null)]
        [InlineData("..", null)]
        [InlineData("a*b", null)]
        [InlineData("satır\nsonu", null)]
        [InlineData("", null)]
        public void Names_AreSanitized(string name, string expected) => Assert.Equal(expected, FileTransfer.SanitizeName(name));

        [Fact]
        public void Clash_AddsANumber()
        {
            File.WriteAllText(Path.Combine(_desktop, "not.txt"), "1");
            Assert.Equal(Path.Combine(_desktop, "not (2).txt"), FileTransfer.UniquePath(_desktop, "not.txt"));
            File.WriteAllText(Path.Combine(_desktop, "not (2).txt"), "2");
            Assert.Equal(Path.Combine(_desktop, "not (3).txt"), FileTransfer.UniquePath(_desktop, "not.txt"));
        }

        [Theory]
        [InlineData("/api/files/7/download?t=abc", "https://pops.example/api/files/7/download?t=abc")]
        [InlineData("https://pops.example/api/files/7/upload?t=x", "https://pops.example/api/files/7/upload?t=x")]
        [InlineData("//evil.example/api/files/7", null)]
        [InlineData("https://evil.example/api/files/7", null)]
        [InlineData("https://pops.example:8443/api/files/7", null)]
        [InlineData("http://pops.example/api/files/7", null)]
        [InlineData("/updates/x.msi", null)]
        [InlineData("/api/../updates/x", null)]
        [InlineData("file:///C:/Windows/win.ini", null)]
        public void Urls_OnlyTheAgentsOwnServer(string value, string expected) =>
            Assert.Equal(expected, FileTransfer.ServerUri(Server, value)?.ToString());

        [Fact]
        public void PlainHttpRemoteServer_NeverTransfers() => Assert.Null(FileTransfer.ServerUri("http://pops.example", "/api/files/7"));

        [Theory]
        [InlineData("kisayol.lnk")]
        [InlineData("site.URL")]
        [InlineData("ekran.scr")]
        public void ExecutableLikeFiles_NeedAllowExec(string name)
        {
            Assert.False(FileTransfer.TryParsePush(Push(name), Server, out _, out string error));
            Assert.Contains("allow_exec", error);
            Assert.True(FileTransfer.TryParsePush(Push(name, allowExec: true), Server, out _, out _));
        }

        [Fact]
        public void PushValidation()
        {
            Assert.True(FileTransfer.TryParsePush(Push(), Server, out var request, out _));
            Assert.Equal(("t-7", "ödev.pdf", "inbox"), (request.TransferId, request.Name, request.Dest));
            Assert.False(FileTransfer.TryParsePush(Push(dest: "C:\\Windows"), Server, out _, out _));
            Assert.False(FileTransfer.TryParsePush(Push(sha: "abc"), Server, out _, out _));
            Assert.False(FileTransfer.TryParsePush(Push(size: FileTransfer.MaxBytes + 1), Server, out _, out _));
            Assert.False(FileTransfer.TryParsePush(Push(url: "https://evil.example/api/x"), Server, out _, out _));
        }

        [Fact]
        public async Task Push_ToInbox_VerifiesAndWrites()
        {
            Assert.True(FileTransfer.TryParsePush(Push(), Server, out var request, out _));
            var (status, path, _) = await FileTransfer.PushAsync(request, "HW-FILES", new DateTime(2026, 10, 5), CancellationToken.None);
            Assert.Equal("done", status);
            Assert.Equal(Path.Combine(AgentUpdate.DataDir, "inbox", "2026-10-05", "ödev.pdf"), path);
            Assert.Equal(_content, File.ReadAllBytes(path));
            Assert.Equal("https://pops.example/api/files/7/download?t=abc", _seen.RequestUri.ToString());
            Assert.Equal(Secret, _seen.Headers.GetValues("X-Agent-Secret").Single());
            Assert.Empty(Directory.GetFiles(SecureStore.Dir, "transfer-*"));   // geçici dosya kalmaz
        }

        [Fact]
        public async Task Push_ToPublicDesktop_AndAClashGetsANumber()
        {
            File.WriteAllText(Path.Combine(_desktop, "ödev.pdf"), "eski");
            Assert.True(FileTransfer.TryParsePush(Push(dest: "public_desktop"), Server, out var request, out _));
            var (status, path, _) = await FileTransfer.PushAsync(request, "HW-FILES", DateTime.Now, CancellationToken.None);
            Assert.Equal("done", status);
            Assert.Equal(Path.Combine(_desktop, "ödev (2).pdf"), path);
            Assert.Equal("eski", File.ReadAllText(Path.Combine(_desktop, "ödev.pdf")));
        }

        [Theory]
        [InlineData(true, false)]
        [InlineData(false, true)]
        public async Task Push_WrongHashOrSize_IsRejected_AndWritesNothing(bool wrongHash, bool wrongSize)
        {
            Assert.True(FileTransfer.TryParsePush(Push(sha: wrongHash ? new string('0', 64) : null, size: wrongSize ? _content.Length - 1 : null), Server, out var request, out _));
            var (status, path, _) = await FileTransfer.PushAsync(request, "HW-FILES", new DateTime(2026, 10, 5), CancellationToken.None);
            Assert.Equal("rejected", status);
            Assert.Null(path);
            Assert.Empty(Directory.GetFiles(Path.Combine(AgentUpdate.DataDir, "inbox", "2026-10-05")));
        }

        // ------------------------------------------------------------------ çekme
        private JsonElement Pull(string path, long max = 1_000_000, bool any = false, string reason = "inceleme") =>
            Json(new { action = "file_pull", transfer_id = "p-1", path, max_size = max, upload = "/api/files/9/upload?t=x", reason, any_profile = any });

        [Theory]
        [InlineData("goreli\\dosya.txt")]
        [InlineData("\\\\sunucu\\pay\\dosya.txt")]
        public void Pull_NeedsALocalFullPath(string path) => Assert.False(FileTransfer.TryParsePull(Pull(path), Server, out _, out _));

        [Fact]
        public void Pull_NeedsAReason() => Assert.Contains("gerekçe", FileTransfer.TryParsePull(Pull("C:\\x.txt", reason: " "), Server, out _, out string e) ? "" : e);

        [Fact]
        public void PullPathRules()
        {
            string secure = SecureStore.Dir;
            Assert.NotNull(FileTransfer.CheckPullPath(Path.Combine(secure, "agent.secret"), secure, _profiles, Path.Combine(_profiles, "ali"), true));
            Assert.NotNull(FileTransfer.CheckPullPath(Path.Combine(_profiles, "ayse", "a.txt"), secure, _profiles, Path.Combine(_profiles, "ali"), false));
            Assert.Null(FileTransfer.CheckPullPath(Path.Combine(_profiles, "ayse", "a.txt"), secure, _profiles, Path.Combine(_profiles, "ali"), true));
            Assert.Null(FileTransfer.CheckPullPath(Path.Combine(_profiles, "ali", "a.txt"), secure, _profiles, Path.Combine(_profiles, "ali"), false));
            Assert.Null(FileTransfer.CheckPullPath(Path.Combine(_profiles, "Public", "a.txt"), secure, _profiles, Path.Combine(_profiles, "ali"), false));
            Assert.Null(FileTransfer.CheckPullPath("D:\\Proje\\a.txt", secure, _profiles, Path.Combine(_profiles, "ali"), false));
            // Konsolda kullanıcı yoksa her profil başkasınındır
            Assert.NotNull(FileTransfer.CheckPullPath(Path.Combine(_profiles, "ali", "a.txt"), secure, _profiles, null, false));
            // "alim" klasörü "ali"nin profili sayılmaz
            Assert.NotNull(FileTransfer.CheckPullPath(Path.Combine(_profiles, "alim", "a.txt"), secure, _profiles, Path.Combine(_profiles, "ali"), false));
        }

        [Fact]
        public async Task Pull_UploadsWithTheDeviceKey()
        {
            string file = Path.Combine(_profiles, "ali", "sonuc.txt");
            File.WriteAllBytes(file, _content);
            Assert.True(FileTransfer.TryParsePull(Pull(file), Server, out var request, out _));
            var (status, path, _, size) = await FileTransfer.PullAsync(request, "HW-FILES", CancellationToken.None);
            Assert.Equal("done", status);
            Assert.Equal(Path.GetFullPath(file), Path.GetFullPath(path));
            Assert.Equal(_content.Length, size);
            Assert.Equal(_content, _uploaded);
            Assert.Equal(HttpMethod.Post, _seen.Method);
            Assert.Equal("HW-FILES", _seen.Headers.GetValues("X-Agent-Id").Single());
        }

        [Fact]
        public async Task Pull_RefusesSecureFiles_OtherProfiles_AndLargeFiles()
        {
            string secret = SecureStore.PathOf("agent.secret");
            Assert.True(FileTransfer.TryParsePull(Pull(secret, any: true), Server, out var r1, out _));
            Assert.Equal("rejected", (await FileTransfer.PullAsync(r1, "HW-FILES", CancellationToken.None)).Status);

            string other = Path.Combine(_profiles, "ayse", "gizli.txt");
            File.WriteAllText(other, "x");
            Assert.True(FileTransfer.TryParsePull(Pull(other), Server, out var r2, out _));
            Assert.Equal("rejected", (await FileTransfer.PullAsync(r2, "HW-FILES", CancellationToken.None)).Status);

            string big = Path.Combine(_profiles, "ali", "buyuk.bin");
            File.WriteAllBytes(big, _content);
            Assert.True(FileTransfer.TryParsePull(Pull(big, max: 100), Server, out var r3, out _));
            Assert.Equal("rejected", (await FileTransfer.PullAsync(r3, "HW-FILES", CancellationToken.None)).Status);
            Assert.Null(_uploaded);
        }

        // ------------------------------------------------------------------ servis
        [Fact]
        public async Task Worker_PushReportsResult_AndTellsTheUser()
        {
            var sent = new List<JsonElement>();
            var tray = new List<string>();
            using var worker = new Worker(NullLogger<Worker>.Instance)
            {
                HwId = "HW-FILES",
                SendOverride = p => { lock (sent) sent.Add(JsonSerializer.SerializeToElement(p)); return Task.FromResult(true); },
                TrayOverride = m => { lock (tray) tray.Add(m); },
            };
            await worker.HandleFileTransferAsync("file_push", Push(), CancellationToken.None);
            await worker.FileTransferTask;
            JsonElement result = Assert.Single(sent);
            Assert.Equal(("file_result", "t-7", "done"), (result.GetProperty("type").GetString(), result.GetProperty("transfer_id").GetString(), result.GetProperty("outcome").GetString()));
            Assert.False(result.TryGetProperty("status", out _));
            Assert.Equal("FILE_PUSHED:ödev.pdf", Assert.Single(tray));

            await worker.HandleFileTransferAsync("file_push", Push(name: "CON"), CancellationToken.None);
            Assert.Equal("rejected", sent.Last().GetProperty("outcome").GetString());
        }

        [Fact]
        public async Task Worker_LocallyDisabledCapability_RefusesBothDirections()
        {
            SecureStore.WriteProtected(SecureStore.PathOf(AgentCapabilities.FileName), "{\"files_enabled\":false}");
            AgentCapabilities.Load();
            var sent = new List<JsonElement>();
            using var worker = new Worker(NullLogger<Worker>.Instance)
            {
                HwId = "HW-FILES",
                SendOverride = p => { lock (sent) sent.Add(JsonSerializer.SerializeToElement(p)); return Task.FromResult(true); },
            };
            await worker.HandleFileTransferAsync("file_pull", Pull("C:\\x.txt"), CancellationToken.None);
            Assert.Contains(sent, m => m.GetProperty("type").GetString() == "capability_denied" && m.GetProperty("capability").GetString() == "files");
            Assert.Contains(sent, m => m.GetProperty("type").GetString() == "file_result" && m.GetProperty("outcome").GetString() == "rejected");
            Assert.Null(_seen);
        }

        [Fact]
        public void AuditEvents_KeepNoContent()
        {
            LocalAuditEvent pushed = LocalAudit.FilePushed("t-7", "C:\\Users\\Public\\Desktop\\a.pdf", 10, new string('a', 64), "ders");
            Assert.Equal((1120, LocalAuditLevel.Warning), (pushed.EventId, pushed.Level));
            Assert.Equal(1121, LocalAudit.FilePulled("p-1", "C:\\a.txt", 10, "inceleme").EventId);
        }
    }
}
