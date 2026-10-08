using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using POpsAgent;
using Xunit;

namespace POps.Tests.Agent
{
    // Sunucu modülleri (0.1.15): politika yanıtındaki "modules" bellekte tutulur; etkin = sunucuda açık VE yerelde izinli
    [Collection(SharedStateCollection.Name)]
    public class ModulesTests : SharedStateTestBase, IDisposable
    {
        private const string Secret = "modules-secret-0123456789abcdefghijkl";
        private readonly Worker _worker;
        private readonly List<JsonElement> _sent = new List<JsonElement>();
        private readonly HttpClient _client = AgentHttp.Client;

        public ModulesTests()
        {
            SecureStore.Dir = TestEnvironment.NewDir("modules-secure");
            AgentUpdate.DataDir = TestEnvironment.NewDir("modules-data");
            AgentCapabilities.Load();   // dosya yok: terminal ve Vision yerelde açık
            AgentCredentials.SaveSecret(Secret, "HW-MOD");
            _worker = new Worker(NullLogger<Worker>.Instance)
            {
                HwId = "HW-MOD",
                SendOverride = payload =>
                {
                    lock (_sent) _sent.Add(JsonSerializer.SerializeToElement(payload));
                    return Task.FromResult(true);
                },
                CommandRunner = TestEnvironment.NewCommandRunner(),
            };
        }

        public void Dispose()
        {
            _worker.Dispose();
            AgentHttp.Client = _client;
            AgentModules.Reset();
            SecureStore.Dir = TestEnvironment.DefaultSecureDir;
            AgentUpdate.DataDir = TestEnvironment.DefaultDataDir;
            AgentCapabilities.Load();
        }

        private sealed class Handler : HttpMessageHandler
        {
            public string Body { get; set; } = "{}";
            public HttpRequestMessage Seen { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Seen = request;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Body, Encoding.UTF8, "application/json") });
            }
        }

        private static string Policy(params (string Module, bool On)[] modules) =>
            "{\"fair_use_text\":\"\",\"dns_domains\":{},\"modules\":{"
            + string.Join(",", modules.Select(m => $"\"{m.Module}\":{(m.On ? "true" : "false")}")) + "}}";

        private void Close(params string[] modules) => _worker.ApplyPolicy(Policy(modules.Select(m => (m, false)).ToArray()));

        private void OpenAll() => _worker.ApplyPolicy(Policy(AllModules.Select(m => (m, true)).ToArray()));

        private static readonly string[] AllModules =
        {
            "vision", "terminal", "deploy", "schedules", "patches", "software", "licenses", "helpdesk", "dns_policy", "quarantine", "wol", "reports",
        };

        private Task Handle(string json) => _worker.HandleServerMessageAsync(json, null, CancellationToken.None);

        private List<JsonElement> Sent(string type)
        {
            lock (_sent) return _sent.Where(m => m.TryGetProperty("type", out var t) && t.GetString() == type).ToList();
        }

        private void ClearSent() { lock (_sent) _sent.Clear(); }

        private JsonElement Denied(string capability, string action) =>
            Assert.Single(Sent("capability_denied"), m => m.GetProperty("capability").GetString() == capability && m.GetProperty("action").GetString() == action);

        private async Task<JsonElement> WaitForResult(int taskId)
        {
            for (int i = 0; i < 200; i++)
            {
                JsonElement? result = Sent("result").Cast<JsonElement?>().FirstOrDefault(m => m.Value.GetProperty("task_id").GetInt32() == taskId);
                if (result != null) return result.Value;
                await Task.Delay(50);
            }
            throw new TimeoutException("sonuç gelmedi: " + taskId);
        }

        [Fact]
        public async Task PolicyRequest_CarriesTheDeviceKey_AndAppliesModules()
        {
            var handler = new Handler { Body = Policy(("terminal", false), ("vision", true)) };
            AgentHttp.Client = new HttpClient(handler);

            string json = await _worker.FetchPolicyJsonAsync(CancellationToken.None);
            Assert.Equal(HttpMethod.Get, handler.Seen.Method);
            Assert.EndsWith("/api/agent_policies", handler.Seen.RequestUri.AbsolutePath);
            Assert.Equal("HW-MOD", handler.Seen.Headers.GetValues("X-Agent-Id").Single());
            Assert.Equal(Secret, handler.Seen.Headers.GetValues("X-Agent-Secret").Single());

            _worker.ApplyPolicy(json);
            Assert.False(AgentModules.IsEnabled(AgentModules.Terminal));
            Assert.True(AgentModules.IsEnabled(AgentModules.Vision));
        }

        [Theory]
        [InlineData("{\"fair_use_text\":\"\",\"dns_domains\":{}}")]
        [InlineData("{\"modules\":null}")]
        [InlineData("{\"modules\":[\"terminal\"]}")]
        public void WithoutModules_EverythingIsOn(string json)
        {
            Close("terminal", "vision", "helpdesk");
            Assert.Equal(new[] { "helpdesk", "terminal", "vision" }, AgentModules.Closed());

            _worker.ApplyPolicy(json);
            Assert.Empty(AgentModules.Closed());
            Assert.All(AllModules, m => Assert.True(AgentModules.IsEnabled(m)));
        }

        [Fact]
        public void Apply_ReportsWhatChanged()
        {
            ModuleChange first = AgentModules.Apply(JsonDocument.Parse(Policy(("terminal", false), ("vision", true))).RootElement);
            Assert.True(first.First);
            Assert.Equal(new[] { "terminal" }, first.Closed);

            ModuleChange same = AgentModules.Apply(JsonDocument.Parse(Policy(("terminal", false))).RootElement);
            Assert.False(same.Any);
            Assert.False(same.First);

            ModuleChange next = AgentModules.Apply(JsonDocument.Parse(Policy(("terminal", true), ("wol", false))).RootElement);
            Assert.Equal(new[] { "wol" }, next.Closed);
            Assert.Equal(new[] { "terminal" }, next.Opened);
        }

        [Fact]
        public async Task Terminal_ClosedModule_DeniesExecute_AndComesBack()
        {
            Close("terminal");
            await Handle("{\"action\":\"execute\",\"task_id\":71,\"script_path\":\"echo modul\"}");
            JsonElement result = await WaitForResult(71);
            Assert.Equal(CommandRunner.ExitDenied, result.GetProperty("exit_code").GetInt32());
            Assert.Equal(CommandExecutionPolicy.ModuleDisabledMessage, result.GetProperty("output").GetString());
            JsonElement denied = Denied("terminal", "execute");
            Assert.Equal(71, denied.GetProperty("task_id").GetInt32());
            Assert.Equal("module_disabled", denied.GetProperty("reason").GetString());

            OpenAll();
            await Handle("{\"action\":\"execute\",\"task_id\":72,\"script_path\":\"echo modul\"}");
            result = await WaitForResult(72);
            Assert.Equal(0, result.GetProperty("exit_code").GetInt32());
            Assert.Contains("modul", result.GetProperty("output").GetString());
        }

        // Yerel yetenek kilidi önce gelir: eski davranış, nedensiz capability_denied
        [Fact]
        public void LocalLock_TakesPrecedence()
        {
            CommandPermission local = CommandExecutionPolicy.Permission(false, false);
            Assert.Equal(CommandExecutionPolicy.DisabledMessage, local.Rejection);
            Assert.Null(local.Reason);
            CommandPermission module = CommandExecutionPolicy.Permission(true, false);
            Assert.False(module.Allowed);
            Assert.Equal("module_disabled", module.Reason);
            Assert.True(CommandExecutionPolicy.Permission(true, true).Allowed);
            Assert.True(CommandExecutionPolicy.Permission(true).Allowed);
        }

        [Theory]
        [InlineData("{\"action\":\"start_stream\",\"fps\":2}", "start_stream")]
        [InlineData("{\"action\":\"start_vision_session\",\"session_id\":\"s1\"}", "start_vision_session")]
        [InlineData("{\"type\":\"remote_input\",\"device\":\"HW-MOD\",\"action\":\"get_thumbnail\"}", "get_thumbnail")]
        public async Task Vision_ClosedModule_NoTunnelNoPreview_AndComesBack(string message, string action)
        {
            Close("vision");
            await Handle(message);
            Assert.Equal("module_disabled", Denied("vision", action).GetProperty("reason").GetString());

            OpenAll();
            ClearSent();
            await Handle(message);
            Assert.Empty(Sent("capability_denied"));
        }

        [Fact]
        public async Task Patches_ClosedModule_DeniesCommands_AndComesBack()
        {
            Close("patches");
            await Handle("{\"action\":\"scan_updates\"}");
            await Handle("{\"action\":\"install_updates\",\"scope\":\"security\"}");
            Assert.Equal("module_disabled", Denied("patches", "scan_updates").GetProperty("reason").GetString());
            Assert.Equal("module_disabled", Denied("patches", "install_updates").GetProperty("reason").GetString());

            OpenAll();
            ClearSent();
            // Geçersiz kapsam: kurulum başlamaz, ama modül nedeniyle reddedilmez
            await Handle("{\"action\":\"install_updates\",\"scope\":\"x\"}");
            Assert.Empty(Sent("capability_denied"));
        }

        [Fact]
        public async Task Patches_ClosedModule_ResultIsNotSent_AndComesBack()
        {
            int posts = 0;
            var patches = new PatchManager("https://pops.example", () => "HW-MOD") { Poster = _ => { posts++; return Task.FromResult(PostResult.Sent); } };
            Close("patches");
            Assert.Equal(PostResult.NotSent, await patches.DeliverAsync(new PatchStatusPayload()));
            Assert.Equal(0, posts);
            Assert.Null(PatchManager.LoadState().PendingReport);

            OpenAll();
            Assert.Equal(PostResult.Sent, await patches.DeliverAsync(new PatchStatusPayload()));
            Assert.Equal(1, posts);
        }

        [Fact]
        public async Task Wol_ClosedModule_NoPeerWake_AndComesBack()
        {
            Close("wol");
            await Handle("{\"action\":\"wake_peer\",\"mac\":\"00\"}");
            Assert.Equal("module_disabled", Denied("wol", "wake_peer").GetProperty("reason").GetString());

            OpenAll();
            ClearSent();
            await Handle("{\"action\":\"wake_peer\",\"mac\":\"00\"}");   // geçersiz MAC: paket gönderilmez
            Assert.Empty(Sent("capability_denied"));
        }

        [Fact]
        public async Task Software_ClosedModule_NotSent_AndResentWhenReopened()
        {
            int posts = 0;
            var reporter = new SoftwareReporter("https://pops.example", () => "HW-MOD")
            {
                Collector = () => new List<SoftwareItem> { new SoftwareItem { Name = "7-Zip", Version = "24.08" } },
                Poster = (_, _) => { posts++; return Task.FromResult(PostResult.Sent); },
            };
            _worker.Software = reporter;
            Assert.Null(await reporter.ReportOnceAsync());
            Assert.Equal(1, posts);

            Close("software");
            Assert.Null(await reporter.ReportOnceAsync());
            Assert.Equal(1, posts);

            // Kapalıyken sunucu listeyi saklamadı: açılınca aynı liste yeniden gönderilir
            OpenAll();
            Assert.Null(await reporter.ReportOnceAsync());
            Assert.Equal(2, posts);
        }

        [Fact]
        public async Task Helpdesk_ClosedModule_HidesTheMenuAndRefuses_AndComesBack()
        {
            var replies = new List<string>();
            var helpdesk = new Helpdesk("https://pops.example", () => "HW-MOD", () => "ogrenci", replies.Add);
            Assert.Equal("HELPDESK_MENU:1", Worker.HelpdeskMenuMessage());

            Close("helpdesk");
            Assert.Equal("HELPDESK_MENU:0", Worker.HelpdeskMenuMessage());
            await helpdesk.CreateAsync("e30=");
            await helpdesk.ListAsync();
            Assert.Equal(2, replies.Count);
            Assert.All(replies, r => Assert.True(SaysDisabled(r)));

            OpenAll();
            Assert.Equal("HELPDESK_MENU:1", Worker.HelpdeskMenuMessage());
            replies.Clear();
            await helpdesk.CreateAsync("e30=");
            Assert.Single(replies);
            Assert.False(SaysDisabled(replies[0]));
        }

        // Tepsiye giden yanıt: "TÜR:" + base64(JSON)
        private static bool SaysDisabled(string reply)
        {
            string json = Encoding.UTF8.GetString(Convert.FromBase64String(reply.Substring(reply.IndexOf(':') + 1)));
            using JsonDocument doc = JsonDocument.Parse(json);
            return doc.RootElement.EnumerateObject().Any(p => p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() == Helpdesk.ModuleDisabledMessage);
        }

        // Modül kapalılığı yalnızca bellekte: capabilities.json'a dokunulmaz, yerel yetenekler değişmez
        [Fact]
        public void ClosedModules_NeverTouchCapabilitiesJson()
        {
            string path = SecureStore.PathOf(AgentCapabilities.FileName);
            Assert.False(File.Exists(path));
            Close(AllModules);
            Assert.False(File.Exists(path));
            Assert.True(AgentCapabilities.TerminalEnabled);
            Assert.True(AgentCapabilities.VisionEnabled);

            // Yerel kilit varken de dosya yalnızca yerel kilitle değişir
            SecureStore.WriteProtected(path, "{\"terminal_enabled\":false,\"vision_enabled\":true}");
            AgentCapabilities.Load();
            byte[] before = File.ReadAllBytes(path);
            DateTime written = File.GetLastWriteTimeUtc(path);
            OpenAll();
            Close("vision", "terminal");
            OpenAll();
            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.Equal(written, File.GetLastWriteTimeUtc(path));
            Assert.False(AgentCapabilities.TerminalEnabled);
        }

        [Fact]
        public void ModuleChange_IsAuditedOnce()
        {
            LocalAuditEvent change = LocalAudit.ModulesChanged(new[] { "terminal", "vision" }, Array.Empty<string>());
            Assert.Equal(1080, change.EventId);
            Assert.Equal(LocalAuditLevel.Information, change.Level);
            Assert.Contains("closed: terminal, vision", change.Message);
            Assert.Contains("opened: -", change.Message);

            // Aynı yanıt ikinci kez: değişiklik yok
            Close("terminal");
            Assert.False(AgentModules.Apply(JsonDocument.Parse(Policy(("terminal", false))).RootElement).Any);
        }
    }
}
