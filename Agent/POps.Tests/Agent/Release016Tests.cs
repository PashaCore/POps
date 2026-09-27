using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net.Http;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using POps.Shared;
using POpsAgent;
using Xunit;

namespace POps.Tests.Agent
{
    // 1) Tepsi/watchdog kullanıcı oturumunda başlatılır (saha: kurulum ve güncellemeden sonra tepsi yoktu)
    public class UserSessionAppsTests : TestBase
    {
        // SYSTEM olarak çalışan bir CI'da WTSQueryUserToken başarılı olur ve süreç gerçekten başlardı: o durumda atlanır
        public static bool SkipLaunchTests => WindowsIdentity.GetCurrent().IsSystem;

        [Fact]
        public void NoSignedInUser_StartsNothing() =>
            Assert.Equal((false, false), UserAppsPolicy.WhatToStart(false, false, false, false, true, TimeSpan.FromHours(1)));

        [Fact]
        public void DuringAnUpdate_StartsNothing() =>
            Assert.Equal((false, false), UserAppsPolicy.WhatToStart(true, true, false, false, true, TimeSpan.FromHours(1)));

        [Fact]
        public void MissingApps_AreStarted() =>
            Assert.Equal((true, true), UserAppsPolicy.WhatToStart(true, false, false, false, true, TimeSpan.Zero));

        [Fact]
        public void RunningApps_AreLeftAlone() =>
            Assert.Equal((false, false), UserAppsPolicy.WhatToStart(true, false, true, true, true, TimeSpan.FromHours(1)));

        [Fact]
        public void Tray_WaitsForTheShellButNotForever()
        {
            // Oturum yeni açıldı, görev çubuğu henüz yok: tepsi simgesi kaybolmasın diye beklenir
            Assert.Equal((false, false), UserAppsPolicy.WhatToStart(true, false, true, false, false, TimeSpan.FromSeconds(10)));
            // Özel kabuk (explorer hiç yok): bir dakika sonra yine de başlatılır
            Assert.Equal((false, true), UserAppsPolicy.WhatToStart(true, false, true, false, false, UserAppsPolicy.ShellWait));
        }

        // Testler SYSTEM değildir: kullanıcı belirteci alınamaz, hiçbir şey başlatılmaz ve istisna çıkmaz
        [Fact]
        public void WithoutSystemRights_NothingIsStarted()
        {
            if (SkipLaunchTests) return;
            string harmless = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "whoami.exe");
            uint session = UserSessionLauncher.ActiveConsoleSession();
            Assert.False(UserSessionLauncher.HasSignedInUser(session));
            Assert.False(UserSessionLauncher.TryStart(session, harmless, out int pid, out string error));
            Assert.Equal(0, pid);
            Assert.False(string.IsNullOrEmpty(error));

            Assert.False(UserSessionLauncher.TryStart(session, @"C:\yok\POpsTray.exe", out _, out string missing));
            Assert.Contains("yok", missing);
            Assert.False(UserSessionLauncher.TryStart(UserSessionLauncher.NoSession, harmless, out _, out _));

            new UserSessionApps(TestEnvironment.NewDir("apps")).EnsureOnce();
        }
    }

    // 2) Yardım masası: tepsi isteği, sunucu sözleşmesi, yanıt mesajları, kullanıcıya göre süzme, yeni yanıtlar
    public class HelpdeskTests : TestBase, IDisposable
    {
        private readonly List<string> _tray = new List<string>();
        private readonly List<(HttpMethod Method, string Path, object Payload)> _requests = new List<(HttpMethod, string, object)>();

        public HelpdeskTests()
        {
            AgentUpdate.DataDir = TestEnvironment.NewDir("helpdesk");
            SecureStore.Dir = TestEnvironment.NewDir("helpdesk-secure");
            AgentCredentials.SaveSecret("test-secret-0123456789abcdefghijklmn", "HW-A");
        }

        public void Dispose() => AgentUpdate.DataDir = TestEnvironment.DefaultDataDir;

        private static string Encode(object value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)));

        private static JsonElement Decode(string trayMessage, string kind)
        {
            Assert.StartsWith(kind + ":", trayMessage);
            return JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(trayMessage.Substring(kind.Length + 1)))).RootElement.Clone();
        }

        private Helpdesk Desk(string user, int? status, string body) => new Helpdesk("https://pops.example", () => "HW-A", () => user, _tray.Add)
        {
            Sender = (method, path, payload, _) =>
            {
                _requests.Add((method, path, payload));
                return Task.FromResult((status, body));
            },
        };

        [Fact]
        public void Request_IsValidatedAndLimited()
        {
            TicketCreatePayload p = Helpdesk.ParseCreate(Encode(new { subject = "  Yazıcı çalışmıyor  ", category = "yazici", body = " Kağıt sıkıştı " }), "ogrenci", out string error);
            Assert.Null(error);
            Assert.Equal("Yazıcı çalışmıyor", p.Subject);
            Assert.Equal("yazici", p.Category);
            Assert.Equal("Kağıt sıkıştı", p.Body);
            Assert.Equal("ogrenci", p.Reporter);

            TicketCreatePayload big = Helpdesk.ParseCreate(Encode(new { subject = new string('k', 300), category = "uydurma", body = new string('a', 6000) }), new string('u', 150), out _);
            Assert.Equal(Helpdesk.MaxSubject, big.Subject.Length);
            Assert.Equal(Helpdesk.MaxBody, big.Body.Length);
            Assert.Equal("diger", big.Category);
            Assert.Equal(Helpdesk.MaxReporter, big.Reporter.Length);

            Assert.Null(Helpdesk.ParseCreate(Encode(new { subject = " ab ", category = "ag" }), "ogrenci", out string shortError));
            Assert.Equal("Konu en az 3 karakter olmalı.", shortError);
            Assert.Null(Helpdesk.ParseCreate("bu-base64-değil", "ogrenci", out _));
            Assert.Null(Helpdesk.ParseCreate(new string('A', Helpdesk.MaxEncodedRequest + 4), "ogrenci", out _));
            Assert.Null(Helpdesk.ParseCreate(Encode(new { subject = "Konu" }), null, out _).Reporter);
        }

        [Fact]
        public void Payload_MatchesServerModel()
        {
            string[] lines = File.ReadAllLines(Path.Combine(TestEnvironment.RepoRoot(), "Backend", "pops", "models.py"));
            int start = Array.FindIndex(lines, l => l.StartsWith("class AgentTicketInput(BaseModel)", StringComparison.Ordinal));
            var fields = new List<string>();
            for (int i = start + 1; i < lines.Length && (lines[i].Length == 0 || char.IsWhiteSpace(lines[i][0])); i++)
            {
                Match m = Regex.Match(lines[i], @"^    (\w+)\s*:");
                if (m.Success) fields.Add(m.Groups[1].Value);
            }
            using JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize(new TicketCreatePayload { Subject = "x" }));
            Assert.Equal(fields.OrderBy(f => f, StringComparer.Ordinal), doc.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(f => f, StringComparer.Ordinal));
        }

        [Theory]
        [InlineData(200, "{\"ok\":true,\"id\":42}", true, "#42")]
        [InlineData(400, "{\"detail\":\"Konu en az 3 karakter olmalı.\"}", false, "Konu en az 3 karakter olmalı.")]
        [InlineData(400, "", false, "Konu en az 3 karakter olmalı.")]
        [InlineData(429, "{\"detail\":\"Bu bilgisayardan çok fazla açık talep var; BT ekibinin yanıtını bekleyin.\"}", false, "çok fazla açık talep")]
        [InlineData(429, null, false, "çok fazla açık talep")]
        [InlineData(401, "{\"detail\":\"x\"}", false, "kayıtlı değil")]
        [InlineData(403, null, false, "kayıtlı değil")]
        [InlineData(404, null, false, "desteklemiyor")]
        [InlineData(500, null, false, "HTTP 500")]
        public void ServerResponses_BecomeTurkishMessages(int status, string body, bool ok, string expected)
        {
            TicketResultMessage result = Helpdesk.CreateResult(status, body);
            Assert.Equal(ok, result.Ok);
            Assert.Contains(expected, result.Message);
        }

        [Fact]
        public void NoResponse_MeansUnreachable() => Assert.Equal(Helpdesk.UnreachableMessage, Helpdesk.CreateResult(null, null).Message);

        [Fact]
        public void ServerDetail_IsSanitized()
        {
            string message = Helpdesk.CreateResult(400, "{\"detail\":\"satır1\\r\\nsatır2" + new string('x', 400) + "\"}").Message;
            Assert.DoesNotContain("\n", message, StringComparison.Ordinal);
            Assert.True(message.Length <= 201);
        }

        [Fact]
        public async Task Create_AddsTheSignedInUserAndRepliesToTheTray()
        {
            await Desk("ogrenci", 200, "{\"ok\":true,\"id\":7}").CreateAsync(Encode(new { subject = "İnternet yok", category = "ag", body = "" }));

            var (method, path, payload) = _requests.Single();
            Assert.Equal(HttpMethod.Post, method);
            Assert.Equal("/api/tickets/agent/HW-A", path);
            Assert.Equal("ogrenci", ((TicketCreatePayload)payload).Reporter);
            JsonElement reply = Decode(_tray.Single(), "TICKET_RESULT");
            Assert.True(reply.GetProperty("ok").GetBoolean());
            Assert.Equal(7, reply.GetProperty("id").GetInt64());
        }

        [Fact]
        public async Task InvalidRequest_NeverReachesTheServer()
        {
            await Desk("ogrenci", 200, "{}").CreateAsync(Encode(new { subject = "a" }));
            Assert.Empty(_requests);
            Assert.False(Decode(_tray.Single(), "TICKET_RESULT").GetProperty("ok").GetBoolean());
        }

        private const string ServerList =
            "[{\"id\":3,\"subject\":\"Ekran\",\"status\":\"open\",\"status_text\":\"açık\",\"reporter\":\"OGRENCI\",\"created_at\":\"2026-09-28T09:00:00+03:00\",\"replies\":[{\"author\":\"bt\",\"body\":\"Bakıyoruz\",\"created_at\":\"2026-09-28T09:10:00+03:00\"}]}," +
            "{\"id\":2,\"subject\":\"Başkasının talebi\",\"status\":\"open\",\"status_text\":\"açık\",\"reporter\":\"diger.ogrenci\",\"replies\":[{\"author\":\"bt\",\"body\":\"gizli kalmalı\"}]}]";

        // Ortak laboratuvar bilgisayarında başkasının talebi ve yanıtları görünmez
        [Fact]
        public async Task List_ShowsOnlyTheSignedInUsersTickets()
        {
            await Desk("ogrenci", 200, ServerList).ListAsync();
            JsonElement reply = Decode(_tray.Single(), "TICKET_LIST_RESULT");
            JsonElement tickets = reply.GetProperty("tickets");
            Assert.Equal(1, tickets.GetArrayLength());
            Assert.Equal(3, tickets[0].GetProperty("id").GetInt64());
            Assert.DoesNotContain("gizli kalmalı", _tray.Single().Length > 0 ? Encoding.UTF8.GetString(Convert.FromBase64String(_tray.Single().Substring("TICKET_LIST_RESULT:".Length))) : "");
            // Görüntülenen yanıtlar için sonra balon çıkmaz
            Assert.Equal(1, Helpdesk.LoadSeen()[3]);
        }

        [Fact]
        public void NewReplies_AreDetectedOnce()
        {
            var seen = new Dictionary<long, int>();
            var ticket = new TicketView { Id = 5, Replies = new List<TicketReply>() };
            Assert.Empty(Helpdesk.NewReplies(new[] { ticket }, seen));      // ilk kez görüldü: bildirim yok
            ticket.Replies.Add(new TicketReply { Author = "bt", Body = "Yanıt" });
            Assert.Single(Helpdesk.NewReplies(new[] { ticket }, seen));     // yeni yanıt
            Assert.Empty(Helpdesk.NewReplies(new[] { ticket }, seen));      // bir kez
        }

        [Fact]
        public void ForUser_WithoutAUser_ShowsNothing()
        {
            var tickets = new[] { new TicketView { Id = 1, Reporter = "ogrenci" } };
            Assert.Empty(Helpdesk.ForUser(tickets, null));
            Assert.Single(Helpdesk.ForUser(tickets, " Ogrenci "));
        }

        [Fact]
        public void TrayMessages_UseSnakeCase()
        {
            using JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize(new TicketNotifyMessage { Id = 1, Subject = "s", StatusText = "açık", NewReplies = 2 }));
            Assert.Equal(new[] { "id", "new_replies", "status_text", "subject" }, doc.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
        }
    }

    // Testler hiçbir zaman gerçek C:\POpsData / C:\POpsData\secure klasörlerine yazmaz: bir test gerçek yolu geri
    // bırakırsa sonraki testin temel kurucusu onu geçici klasöre çevirir
    public class TestIsolationTests : TestBase
    {
        private sealed class NextTest : TestBase { }

        [Fact]
        public void RealFoldersAreNeverUsed()
        {
            AgentUpdate.DataDir = @"C:\POpsData";
            SecureStore.Dir = @"C:\POpsData\secure";
            _ = new NextTest();
            Assert.Equal(TestEnvironment.DefaultDataDir, AgentUpdate.DataDir);
            Assert.Equal(TestEnvironment.DefaultSecureDir, SecureStore.Dir);
            Assert.StartsWith(System.IO.Path.GetTempPath(), AgentUpdate.DataDir, StringComparison.OrdinalIgnoreCase);
        }
    }

    // 3) Tepsi, borunun servise ait olduğunu sahibinden anlar (L13)
    public class PipeOwnerTests : TestBase
    {
        [Fact]
        public void OnlySystemOrAdministratorsAreTrusted()
        {
            Assert.True(PipeOwner.IsTrustedOwner(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)));
            Assert.True(PipeOwner.IsTrustedOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)));
            Assert.False(PipeOwner.IsTrustedOwner(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null)));
            Assert.False(PipeOwner.IsTrustedOwner(WindowsIdentity.GetCurrent().User));
            Assert.False(PipeOwner.IsTrustedOwner(null));
        }

        // Servisin borusuyla aynı izin düzeniyle (SYSTEM ve Administrators tam, etkileşimli kullanıcı ReadWrite):
        // istemci sahibi okuyabilmeli; test kullanıcısının açtığı boru güvenilir sayılmamalı
        [Fact]
        public async Task OwnerIsReadThroughTheClientConnection()
        {
            string name = "POpsTest_" + Guid.NewGuid().ToString("N");
            var ps = new PipeSecurity();
            ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
            ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
            ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null), PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));
            // CI oturumu etkileşimli olmayabilir: bağlanabilmek için test kullanıcısına da aynı hak
            ps.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User, PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));

            using NamedPipeServerStream server = NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, ps);
            Task accept = server.WaitForConnectionAsync();
            using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(5000);
            await accept;

            var actualOwner = (SecurityIdentifier)server.GetAccessControl().GetOwner(typeof(SecurityIdentifier));
            bool trusted = PipeOwner.Check(client, out string owner);
            Assert.Equal(actualOwner.Value, owner);
            Assert.Equal(PipeOwner.IsTrustedOwner(actualOwner), trusted);
        }
    }
}
