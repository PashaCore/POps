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

        // Sunucunun örneklerindeki aktarım kimlikleri (docs/protocol/examples/server-to-agent/file_push.json, file_pull.json)
        private const string PushId = "Wt3q9xLZr0bYc2VnQ1sT4A", PullId = "Kq2PzT8vN5mR1xC7bL4wYg";

        private JsonElement Push(string name = "ödev.pdf", string dest = "inbox", string sha = null, long? size = null, bool allowExec = false,
            string url = "/api/files/" + PushId + "/download?t=abc", string transferId = PushId, string reason = "ders notu") =>
            Json(new { action = "file_push", transfer_id = transferId, name, size = size ?? _content.Length, sha256 = sha ?? Sha(_content), url, dest, reason, allow_exec = allowExec });

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

        // Yalnızca sunucu şemasındaki göreli biçim; sorgu dizisi (tek kullanımlık jeton) korunur
        [Theory]
        [InlineData("/api/files/Wt3q9xLZr0bYc2VnQ1sT4A/download?t=6sQk-R0f_Y1", false, "https://pops.example/api/files/Wt3q9xLZr0bYc2VnQ1sT4A/download?t=6sQk-R0f_Y1")]
        [InlineData("/api/files/Kq2PzT8vN5mR1xC7bL4wYg/upload?t=Hn3b", true, "https://pops.example/api/files/Kq2PzT8vN5mR1xC7bL4wYg/upload?t=Hn3b")]
        [InlineData("/api/files/Kq2PzT8vN5mR1xC7bL4wYg/upload?t=Hn3b", false, null)]             // indirme yerine yükleme adresi
        [InlineData("/api/files/Wt3q9xLZr0bYc2VnQ1sT4A/download?t=abc", true, null)]
        [InlineData("https://pops.example/api/files/Wt3q9xLZr0bYc2VnQ1sT4A/download?t=abc", false, null)]   // mutlak adres yok
        [InlineData("//evil.example/api/files/Wt3q9xLZr0bYc2VnQ1sT4A/download?t=abc", false, null)]
        [InlineData("https://evil.example/api/files/Wt3q9xLZr0bYc2VnQ1sT4A/download?t=abc", false, null)]
        [InlineData("/api/files/7/download?t=abc", false, null)]                                   // kimlik en az 8 karakter
        [InlineData("/api/files/Wt3q9xLZr0bYc2VnQ1sT4A/download", false, null)]                    // jeton yok
        [InlineData("/api/files/Wt3q9xLZr0bYc2VnQ1sT4A/download?t=abc&x=1", false, null)]
        [InlineData("/api/files/Wt3q9xLZr0bYc2VnQ1sT4A/download?t=abc\n", false, null)]
        [InlineData("/api/files/../updates/download?t=abc", false, null)]
        [InlineData("/updates/x.msi", false, null)]
        [InlineData("file:///C:/Windows/win.ini", false, null)]
        public void Urls_OnlyTheServersFormOnTheAgentsOwnServer(string value, bool upload, string expected) =>
            Assert.Equal(expected, FileTransfer.ServerUri(Server, value, upload)?.ToString());

        // Sunucu bir yol önekinin arkasındaysa öteki istekler gibi ServerUrl'e eklenir
        [Fact]
        public void Urls_KeepTheServerPathPrefix() =>
            Assert.Equal("https://pops.example/pops/api/files/Wt3q9xLZr0bYc2VnQ1sT4A/download?t=abc",
                FileTransfer.ServerUri("https://pops.example/pops/", "/api/files/Wt3q9xLZr0bYc2VnQ1sT4A/download?t=abc", false)?.ToString());

        [Fact]
        public void PlainHttpRemoteServer_NeverTransfers() =>
            Assert.Null(FileTransfer.ServerUri("http://pops.example", "/api/files/Wt3q9xLZr0bYc2VnQ1sT4A/download?t=abc", false));

        [Theory]
        [InlineData(null)]
        [InlineData("t-7")]                      // 8 karakterden kısa
        [InlineData("Wt3q9xLZr0bYc2VnQ1sT4A/x")]
        [InlineData("Wt3q9xLZr0bYc2VnQ1sT4A\n")]
        public void InvalidTransferIds_AreNotAccepted(string id)
        {
            Assert.Null(FileTransfer.TransferIdOf(Push(transferId: id)));
            Assert.False(FileTransfer.TryParsePush(Push(transferId: id), Server, out _, out string error));
            Assert.Contains("transfer_id", error);
        }

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
            Assert.Equal((PushId, "ödev.pdf", "inbox"), (request.TransferId, request.Name, request.Dest));
            Assert.False(FileTransfer.TryParsePush(Push(dest: "C:\\Windows"), Server, out _, out _));
            Assert.False(FileTransfer.TryParsePush(Push(sha: "abc"), Server, out _, out _));
            Assert.False(FileTransfer.TryParsePush(Push(size: FileTransfer.MaxBytes + 1), Server, out _, out _));
            Assert.False(FileTransfer.TryParsePush(Push(url: "https://evil.example/api/x"), Server, out _, out _));
            // Gerekçe zorunlu (sunucu şeması: en az 3 karakter)
            Assert.False(FileTransfer.TryParsePush(Push(reason: " ab "), Server, out _, out string error));
            Assert.Contains("gerekçe", error);
            Assert.False(FileTransfer.TryParsePush(Push(reason: null), Server, out _, out _));
        }

        // Sunucunun örnek mesajları (docs/protocol/examples/server-to-agent/file_push.json, file_pull.json)
        private const string VectorFilePush = @"{
  ""action"": ""file_push"",
  ""transfer_id"": ""Wt3q9xLZr0bYc2VnQ1sT4A"",
  ""name"": ""Ödev föyü 3.pdf"",
  ""size"": 1500000,
  ""sha256"": ""9f6e2c1b0a5d4c3e2f1a0b9c8d7e6f5a4b3c2d1e0f9a8b7c6d5e4f3a2b1c0d9e"",
  ""url"": ""/api/files/Wt3q9xLZr0bYc2VnQ1sT4A/download?t=6sQkR0fY1bV2nC3mX4zL5aP6oI7uY8tR9eW0qA1sD2f"",
  ""dest"": ""inbox"",
  ""reason"": ""9-A ödev dosyası"",
  ""allow_exec"": false
}";
        private const string VectorFilePull = @"{
  ""action"": ""file_pull"",
  ""transfer_id"": ""Kq2PzT8vN5mR1xC7bL4wYg"",
  ""path"": ""C:\\Users\\Public\\Documents\\rapor.pdf"",
  ""max_size"": 10485760,
  ""upload"": ""/api/files/Kq2PzT8vN5mR1xC7bL4wYg/upload?t=Hn3bV5cX7zL9kJ1gF3dS5aP7oI9uY1tR3eW5qA7sD9f"",
  ""reason"": ""Sınav dosyasının kontrolü"",
  ""any_profile"": false
}";

        private static JsonElement Parse(string text) => JsonDocument.Parse(text).RootElement.Clone();

        [Fact]
        public void Vectors_AreAccepted()
        {
            Assert.True(FileTransfer.TryParsePush(Parse(VectorFilePush), Server, out var push, out string error), error);
            Assert.Equal(("Wt3q9xLZr0bYc2VnQ1sT4A", "Ödev föyü 3.pdf", 1500000L, "inbox", false), (push.TransferId, push.Name, push.Size, push.Dest, push.AllowExec));
            Assert.Equal("https://pops.example/api/files/Wt3q9xLZr0bYc2VnQ1sT4A/download?t=6sQkR0fY1bV2nC3mX4zL5aP6oI7uY8tR9eW0qA1sD2f", push.Url.ToString());
            Assert.True(FileTransfer.TryParsePull(Parse(VectorFilePull), Server, out var pull, out error), error);
            Assert.Equal(("Kq2PzT8vN5mR1xC7bL4wYg", "C:\\Users\\Public\\Documents\\rapor.pdf", 10485760L, false), (pull.TransferId, pull.Path, pull.MaxSize, pull.AnyProfile));
            Assert.Equal("https://pops.example/api/files/Kq2PzT8vN5mR1xC7bL4wYg/upload?t=Hn3bV5cX7zL9kJ1gF3dS5aP7oI9uY1tR3eW5qA7sD9f", pull.Upload.ToString());
        }

        // Sunucunun file_result şeması: type, transfer_id, outcome zorunlu; path ve detail; başka alan yok
        private static void AssertFileResultShape(JsonElement result)
        {
            Assert.All(result.EnumerateObject(), p => Assert.Contains(p.Name, new[] { "type", "transfer_id", "outcome", "path", "detail" }));
            Assert.Equal("file_result", result.GetProperty("type").GetString());
            Assert.Matches("^[A-Za-z0-9_-]{8,64}$", result.GetProperty("transfer_id").GetString());
            Assert.Contains(result.GetProperty("outcome").GetString(), new[] { "done", "rejected", "failed" });
            if (result.TryGetProperty("path", out JsonElement path)) Assert.True(path.GetString().Length <= 1024);
            if (result.TryGetProperty("detail", out JsonElement detail)) Assert.True(detail.GetString().Length <= 500);
        }

        private static void AssertSameJson(string expected, object actual) =>
            Assert.Equal(JsonSerializer.Serialize(Parse(expected)), JsonSerializer.Serialize(JsonSerializer.SerializeToElement(actual)));

        [Fact]
        public void Result_MatchesTheServerExamples()
        {
            AssertSameJson(@"{
  ""type"": ""file_result"",
  ""transfer_id"": ""Wt3q9xLZr0bYc2VnQ1sT4A"",
  ""outcome"": ""done"",
  ""path"": ""C:\\ProgramData\\POps\\Inbox\\Ödev föyü 3.pdf""
}", FileTransfer.Result("Wt3q9xLZr0bYc2VnQ1sT4A", "done", "C:\\ProgramData\\POps\\Inbox\\Ödev föyü 3.pdf"));
            AssertSameJson(@"{
  ""type"": ""file_result"",
  ""transfer_id"": ""Kq2PzT8vN5mR1xC7bL4wYg"",
  ""outcome"": ""rejected"",
  ""detail"": ""[REDDEDİLDİ] Yol izin verilen klasörlerin dışında.""
}", FileTransfer.Result("Kq2PzT8vN5mR1xC7bL4wYg", "rejected", detail: "[REDDEDİLDİ] Yol izin verilen klasörlerin dışında."));
            // Şemadan uzun yol gönderilmez; açıklama kısaltılır
            JsonElement longOne = JsonSerializer.SerializeToElement(FileTransfer.Result(PushId, "failed", "C:\\" + new string('a', 1100), new string('x', 900)));
            AssertFileResultShape(longOne);
            Assert.False(longOne.TryGetProperty("path", out _));
        }

        // capabilities: sunucu örneği capabilities.files.json ile aynı alanlar ve sıra (exam_enabled yok)
        [Fact]
        public void Capabilities_HaveTheServerKeys()
        {
            AgentCapabilities.Load();
            Assert.Equal(new[] { "type", "terminal_enabled", "vision_enabled", "server_ca", "files_enabled" }, AgentCapabilities.StatusMessage().Keys);
            Assert.Equal(true, AgentCapabilities.StatusMessage()[AgentCapabilities.Files]);
        }

        [Fact]
        public void AgentFeatures_AnnounceFiles()
        {
            Assert.Contains("files", AgentFeatures.All);
            Assert.Matches("^[a-z0-9_]+(,[a-z0-9_]+)*$", AgentFeatures.Header);
        }

        [Fact]
        public async Task Push_ToInbox_VerifiesAndWrites()
        {
            Assert.True(FileTransfer.TryParsePush(Push(), Server, out var request, out _));
            var (status, path, _) = await FileTransfer.PushAsync(request, "HW-FILES", new DateTime(2026, 10, 5), CancellationToken.None);
            Assert.Equal("done", status);
            Assert.Equal(Path.Combine(AgentUpdate.DataDir, "inbox", "2026-10-05", "ödev.pdf"), path);
            Assert.Equal(_content, File.ReadAllBytes(path));
            Assert.Equal("https://pops.example/api/files/" + PushId + "/download?t=abc", _seen.RequestUri.ToString());
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
        public async Task Push_WrongHashOrSize_Fails_AndWritesNothing(bool wrongHash, bool wrongSize)
        {
            Assert.True(FileTransfer.TryParsePush(Push(sha: wrongHash ? new string('0', 64) : null, size: wrongSize ? _content.Length - 1 : null), Server, out var request, out _));
            var (outcome, path, _) = await FileTransfer.PushAsync(request, "HW-FILES", new DateTime(2026, 10, 5), CancellationToken.None);
            Assert.Equal("failed", outcome);   // sunucu şeması: sağlama hatası "failed"
            Assert.Null(path);
            Assert.Empty(Directory.GetFiles(Path.Combine(AgentUpdate.DataDir, "inbox", "2026-10-05")));
        }

        // ------------------------------------------------------------------ çekme
        private JsonElement Pull(string path, long max = 1_000_000, bool any = false, string reason = "inceleme") =>
            Json(new { action = "file_pull", transfer_id = PullId, path, max_size = max, upload = "/api/files/" + PullId + "/upload?t=x", reason, any_profile = any });

        // Sunucu şeması: ^[A-Za-z]:\\, en çok 1024 karakter; ağ, aygıt yolu, joker ve ADS yok
        [Theory]
        [InlineData("goreli\\dosya.txt")]
        [InlineData("\\\\sunucu\\pay\\dosya.txt")]
        [InlineData("\\\\?\\C:\\dosya.txt")]
        [InlineData("C:dosya.txt")]
        [InlineData("C:\\dosya.txt:gizli")]
        [InlineData("C:\\*.txt")]
        [InlineData("C:\\dosya?.txt")]
        public void Pull_NeedsALocalFullPath(string path) => Assert.False(FileTransfer.TryParsePull(Pull(path), Server, out _, out _));

        [Fact]
        public void Pull_PathAtMost1024Characters()
        {
            string longest = "C:\\" + new string('a', FileTransfer.MaxPathLength - 3);
            Assert.True(FileTransfer.TryParsePull(Pull(longest), Server, out _, out string error), error);
            Assert.False(FileTransfer.TryParsePull(Pull(longest + "a"), Server, out _, out _));
        }

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
            Assert.Equal("rejected", (await FileTransfer.PullAsync(r1, "HW-FILES", CancellationToken.None)).Outcome);

            string other = Path.Combine(_profiles, "ayse", "gizli.txt");
            File.WriteAllText(other, "x");
            Assert.True(FileTransfer.TryParsePull(Pull(other), Server, out var r2, out _));
            Assert.Equal("rejected", (await FileTransfer.PullAsync(r2, "HW-FILES", CancellationToken.None)).Outcome);

            string big = Path.Combine(_profiles, "ali", "buyuk.bin");
            File.WriteAllBytes(big, _content);
            Assert.True(FileTransfer.TryParsePull(Pull(big, max: 100), Server, out var r3, out _));
            Assert.Equal("rejected", (await FileTransfer.PullAsync(r3, "HW-FILES", CancellationToken.None)).Outcome);
            Assert.Null(_uploaded);
        }

        // ------------------------------------------------------------------ servis
        private readonly List<JsonElement> _sent = new List<JsonElement>();
        private readonly List<string> _tray = new List<string>();

        private Worker NewWorker() => new Worker(NullLogger<Worker>.Instance)
        {
            HwId = "HW-FILES",
            SendOverride = p => { lock (_sent) _sent.Add(JsonSerializer.SerializeToElement(p)); return Task.FromResult(true); },
            TrayOverride = m => { lock (_tray) _tray.Add(m); },
        };

        private static void DisableFilesLocally()
        {
            SecureStore.WriteProtected(SecureStore.PathOf(AgentCapabilities.FileName), "{\"files_enabled\":false}");
            AgentCapabilities.Load();
            Assert.False(AgentCapabilities.FilesEnabled);
        }

        [Fact]
        public async Task Worker_PushReportsResult_AndTellsTheUser()
        {
            using Worker worker = NewWorker();
            await worker.HandleFileTransferAsync("file_push", Push(), CancellationToken.None);
            await worker.FileTransferTask;
            JsonElement result;
            lock (_sent) result = Assert.Single(_sent);
            AssertFileResultShape(result);
            Assert.Equal(("file_result", PushId, "done"), (result.GetProperty("type").GetString(), result.GetProperty("transfer_id").GetString(), result.GetProperty("outcome").GetString()));
            Assert.False(result.TryGetProperty("status", out _));
            Assert.Equal("FILE_PUSHED:ödev.pdf", Assert.Single(_tray));

            await worker.HandleFileTransferAsync("file_push", Push(name: "CON"), CancellationToken.None);
            AssertFileResultShape(_sent.Last());
            Assert.Equal("rejected", _sent.Last().GetProperty("outcome").GetString());
        }

        // Yetenek kapalı: yalnızca bir capability_denied (transfer_id ile; sunucu aktarımı ondan kapatır), file_result yok,
        // hiçbir şey indirilmez ya da yüklenmez. Görevler gibi dakikada bir sınırına takılmaz.
        [Fact]
        public async Task Worker_LocallyDisabledCapability_DeniesWithTheTransferId()
        {
            DisableFilesLocally();
            using Worker worker = NewWorker();
            await worker.HandleServerMessageAsync(VectorFilePull, null, CancellationToken.None);
            JsonElement denied;
            lock (_sent) denied = Assert.Single(_sent);
            // Sunucu örneği capability_denied.files.json
            AssertSameJson(@"{
  ""type"": ""capability_denied"",
  ""capability"": ""files"",
  ""action"": ""file_pull"",
  ""transfer_id"": ""Kq2PzT8vN5mR1xC7bL4wYg""
}", denied);

            await worker.HandleServerMessageAsync(VectorFilePush, null, CancellationToken.None);
            await worker.FileTransferTask;
            lock (_sent)
            {
                Assert.Equal(2, _sent.Count);
                Assert.Equal(("capability_denied", "file_push", PushId), (_sent[1].GetProperty("type").GetString(), _sent[1].GetProperty("action").GetString(), _sent[1].GetProperty("transfer_id").GetString()));
                Assert.DoesNotContain(_sent, m => m.GetProperty("type").GetString() == "file_result");
            }
            Assert.Null(_seen);
            Assert.Empty(_tray);
        }

        // Geçersiz ya da eksik kimlik: file_result gönderilmez (sunucu bilmediği aktarımı yok sayar; şemaya da uymaz)
        [Theory]
        [InlineData(null)]
        [InlineData("t-7")]
        public async Task Worker_InvalidTransferId_SendsNoResult(string id)
        {
            using Worker worker = NewWorker();
            await worker.HandleFileTransferAsync("file_push", Push(transferId: id), CancellationToken.None);
            await worker.FileTransferTask;
            lock (_sent) Assert.Empty(_sent);
            Assert.Null(_seen);

            // Yetenek kapalıysa yine capability_denied gider, transfer_id olmadan
            DisableFilesLocally();
            await worker.HandleFileTransferAsync("file_pull", Json(new { action = "file_pull", transfer_id = id }), CancellationToken.None);
            JsonElement denied;
            lock (_sent) denied = Assert.Single(_sent);
            Assert.Equal("capability_denied", denied.GetProperty("type").GetString());
            Assert.False(denied.TryGetProperty("transfer_id", out _));
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
