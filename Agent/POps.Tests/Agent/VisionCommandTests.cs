using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using POpsAgent;
using Xunit;

namespace POps.Tests.Agent
{
    // Vision komutları ve oturumun sırası (Worker.cs bölmesinin a3 adımında VisionHandler, RemoteInputHandler,
    // CapabilitiesHandler ve VisionSession'a taşındı; bu testler taşımadan önce de aynı sonucu verir). Gerçek Vision tüneli
    // ve tepsi borusu yok: tünel VisionTunnelOverride ile açılır, tepsi TrayOverride'dır, denetim kayıtları ve sunucuya
    // giden mesajlar tek bir sıralı listeye yazılır.
    [Collection(SharedStateCollection.Name)]
    public class VisionCommandTests : SharedStateTestBase, IDisposable
    {
        private const string HwId = "HW-VIS";

        private readonly Worker _worker;
        // Sırayla: "tray:<mesaj>", "send:<type>", "audit:<olay no>"
        private readonly List<string> _events = new List<string>();
        private readonly List<JsonElement> _sent = new List<JsonElement>();
        private readonly List<LocalAuditEvent> _audit = new List<LocalAuditEvent>();
        private int _tunnelOpens;

        public VisionCommandTests()
        {
            SecureStore.Dir = TestEnvironment.NewDir("vision-commands-secure");
            AgentCapabilities.Load();   // dosya yok: Vision açık
            _worker = new Worker(NullLogger<Worker>.Instance)
            {
                HwId = HwId,
                TrayOverride = m => { lock (_events) _events.Add("tray:" + m); },
                SendOverride = p =>
                {
                    JsonElement message = JsonSerializer.SerializeToElement(p);
                    lock (_events)
                    {
                        _sent.Add(message);
                        _events.Add("send:" + (message.TryGetProperty("type", out JsonElement t) ? t.GetString() : "?"));
                    }
                    return Task.FromResult(true);
                },
                VisionTunnelOverride = () => { _tunnelOpens++; return Task.FromResult(true); },
                AuditOverride = e => { lock (_events) { _audit.Add(e); _events.Add("audit:" + e.EventId); } },
                DeviceLogOverride = _ => Task.CompletedTask,
            };
        }

        public void Dispose()
        {
            _worker.Dispose();
            AgentModules.Reset();
            SecureStore.Dir = TestEnvironment.NewDir("vision-commands-reset");
            AgentCapabilities.Load();
        }

        private Task Server(string json) => _worker.HandleServerMessageAsync(json, null, CancellationToken.None);

        private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

        private int[] AuditIds() => _audit.Select(e => e.EventId).ToArray();

        private List<string> Tray() => _events.Where(e => e.StartsWith("tray:", StringComparison.Ordinal)).Select(e => e.Substring(5)).ToList();

        private List<JsonElement> Denied() =>
            _sent.Where(m => m.GetProperty("type").GetString() == "capability_denied").ToList();

        private void VisionCapabilityOff()
        {
            SecureStore.WriteProtected(SecureStore.PathOf(AgentCapabilities.FileName), "{\"terminal_enabled\":true,\"vision_enabled\":false}");
            AgentCapabilities.Load();
        }

        private void VisionModule(bool on) =>
            _worker.ApplyPolicy("{\"fair_use_text\":\"\",\"dns_domains\":{},\"modules\":{\"vision\":" + (on ? "true" : "false") + "}}");

        // ------------------------------------------------------------------ start_stream / stop_stream

        [Fact]
        public async Task StartStream_IsAnUnapprovedSession_AuditedOnce_StopStreamEndsIt()
        {
            await Server("{\"action\":\"start_stream\",\"fps\":3}");
            Assert.Equal(1, _tunnelOpens);
            Assert.Equal(new[] { 1010 }, AuditIds());
            Assert.Contains("user_approved: False", _audit[0].Message);
            Assert.Contains("session_id: (yok)", _audit[0].Message);
            Assert.Equal(new[] { "START_CAPTURE:3" }, Tray());

            // Süren oturumda ikinci start_stream yeniden yakalatır, ikinci 1010 yazmaz; fps sayı değilse 2
            await Server("{\"action\":\"start_stream\",\"fps\":\"x\"}");
            Assert.Equal(new[] { 1010 }, AuditIds());
            Assert.Equal(new[] { "START_CAPTURE:3", "START_CAPTURE:2" }, Tray());

            await Server("{\"action\":\"stop_stream\"}");
            Assert.Equal(new[] { 1010, 1011 }, AuditIds());
            Assert.Empty(_sent);

            // Bitmiş oturum yeniden başlarsa yeniden yazılır
            await Server("{\"action\":\"start_stream\"}");
            Assert.Equal(new[] { 1010, 1011, 1010 }, AuditIds());
            Assert.Equal("START_CAPTURE:2", Tray().Last());
        }

        [Fact]
        public async Task StartVisionSession_GoesToTheTrayVerbatim_AndStartsNothingByItself()
        {
            const string request = "{\"action\":\"start_vision_session\",\"session_id\":\"SES-00000000BEEF\",\"is_mandatory\":false,\"requested_by\":\"Ayşe\"}";
            await Server(request);
            Assert.Equal(new[] { request }, Tray());
            Assert.Equal(0, _tunnelOpens);
            Assert.Empty(_audit);
            Assert.Empty(_sent);

            // Tepsi kullanıcının onayıyla başlattı: oturumun bilgileri istekten
            await _worker.StartVisionFromTrayAsync("START_VISION_TUNNEL:4");
            Assert.Equal(new[] { 1010 }, AuditIds());
            Assert.Contains("session_id: SES-00000000BEEF", _audit[0].Message);
            Assert.Contains("requested_by: Ayşe", _audit[0].Message);
            Assert.Contains("user_approved: True", _audit[0].Message);
            Assert.Equal("START_CAPTURE:4", Tray().Last());
        }

        // ------------------------------------------------------------------ iki anahtar da kapalı: önce yerel yetenek

        [Theory]
        [InlineData("{\"action\":\"start_stream\",\"fps\":2}", "start_stream")]
        [InlineData("{\"action\":\"start_vision_session\",\"session_id\":\"s1\"}", "start_vision_session")]
        [InlineData("{\"type\":\"remote_input\",\"device\":\"HW-VIS\",\"action\":\"get_thumbnail\"}", "get_thumbnail")]
        [InlineData("{\"type\":\"remote_input\",\"device\":\"HW-VIS\",\"input_type\":\"keyboard\",\"key\":\"a\"}", "remote_input")]
        public async Task BothVisionSwitchesOff_TheLocalCapabilityIsReported_NotTheModule(string message, string action)
        {
            VisionCapabilityOff();
            VisionModule(on: false);
            await Server(message);
            JsonElement denied = Assert.Single(Denied());
            Assert.Equal("vision", denied.GetProperty("capability").GetString());
            Assert.Equal(action, denied.GetProperty("action").GetString());
            Assert.False(denied.TryGetProperty("reason", out _));
            Assert.Equal(0, _tunnelOpens);
            Assert.Empty(Tray());
        }

        // ------------------------------------------------------------------ remote_input

        [Fact]
        public async Task RemoteInput_ForThisPc_NeedsAnApprovedSession_AnotherPcIsIgnored()
        {
            await Server("{\"type\":\"remote_input\",\"device\":\"HW-BASKA\",\"input_type\":\"keyboard\",\"key\":\"a\"}");
            Assert.Empty(_sent);

            await Server("{\"type\":\"remote_input\",\"device\":\"HW-VIS\",\"input_type\":\"mouse\",\"x\":1,\"y\":1}");
            JsonElement denied = Assert.Single(Denied());
            Assert.Equal("consent", denied.GetProperty("capability").GetString());
            Assert.Equal("remote_input", denied.GetProperty("action").GetString());

            // Onaylı oturumda girdi tepsiye gider (borudan; burada boru yok), ret gitmez
            await _worker.StartVisionFromTrayAsync("START_VISION_TUNNEL:2");
            await Server("{\"type\":\"remote_input\",\"device\":\"HW-VIS\",\"input_type\":\"mouse\",\"x\":2,\"y\":2}");
            Assert.Single(Denied());

            // Önizleme girdi değildir: onaysız da reddedilmez
            await Server("{\"action\":\"stop_stream\"}");
            await Server("{\"type\":\"remote_input\",\"device\":\"HW-VIS\",\"action\":\"get_thumbnail\"}");
            Assert.Single(Denied());
        }

        // ------------------------------------------------------------------ set_capabilities

        [Fact]
        public async Task SetCapabilities_VisionOff_EndsTheStream_BeforeReportingTheState()
        {
            await Server("{\"action\":\"start_stream\",\"fps\":2}");
            await Server("{\"action\":\"set_capabilities\",\"vision_enabled\":false}");
            Assert.False(AgentCapabilities.VisionEnabled);
            int finished = _events.IndexOf("audit:1011");
            int reported = _events.IndexOf("send:capabilities");
            Assert.True(finished >= 0 && reported > finished, string.Join(" | ", _events));
            Assert.Single(_sent);

            await Server("{\"type\":\"remote_input\",\"device\":\"HW-VIS\",\"input_type\":\"mouse\",\"x\":1,\"y\":1}");
            Assert.Equal("vision", Assert.Single(Denied()).GetProperty("capability").GetString());
        }

        [Fact]
        public async Task SetCapabilities_WithoutAStream_OnlyReportsTheState()
        {
            await Server("{\"action\":\"set_capabilities\",\"vision_enabled\":false}");
            Assert.Empty(_audit);
            Assert.Equal(new[] { "send:capabilities" }, _events);
        }

        // ------------------------------------------------------------------ modül, bağlantı

        [Fact]
        public async Task VisionModuleClosedDuringAStream_EndsTheSession()
        {
            await Server("{\"action\":\"start_stream\",\"fps\":2}");
            VisionModule(on: false);
            Assert.Equal(new[] { 1010, 1011 }, AuditIds());

            await Server("{\"action\":\"start_stream\",\"fps\":2}");
            Assert.Equal("module_disabled", Assert.Single(Denied()).GetProperty("reason").GetString());
            Assert.Equal(1, _tunnelOpens);
        }

        [Fact]
        public async Task LostServerConnection_EndsTheStream_AndANewOneIsAuditedAgain()
        {
            await Server("{\"action\":\"start_stream\",\"fps\":2}");
            await _worker.OnCommandConnectionLostAsync();
            Assert.Equal(new[] { 1010, 1011 }, AuditIds());

            // Oturum yokken bağlantı kaybı hiçbir şey yazmaz
            await _worker.OnCommandConnectionLostAsync();
            Assert.Equal(new[] { 1010, 1011 }, AuditIds());

            await Server("{\"action\":\"start_stream\",\"fps\":2}");
            Assert.Equal(new[] { 1010, 1011, 1010 }, AuditIds());
        }

        // ------------------------------------------------------------------ görüntüleyici denetimi (Vision v2)

        [Fact]
        public async Task ViewerControls_FollowTheApprovedSession()
        {
            _worker.OnCommandSocketOpened();
            _worker.Handshake.OnServerInfo(Json("{\"action\":\"server_info\",\"features\":[\"vision_binary\"]}"));
            await Server("{\"action\":\"start_vision_session\",\"session_id\":\"SES-00000000CAFE\",\"is_mandatory\":false,\"requested_by\":\"Ayşe\"}");
            await _worker.StartVisionFromTrayAsync("START_VISION_TUNNEL:2");
            Assert.Equal(new[] { "START_CAPTURE_V2:2", "CLIPBOARD_SHARE:1" }, Tray().Skip(1));

            await _worker.HandleVisionControlAsync(Json("{\"action\":\"select_monitor\",\"index\":1}"));
            Assert.StartsWith("VISION_SELECT:", Tray().Last(), StringComparison.Ordinal);
            await _worker.HandleVisionControlAsync(Json("{\"action\":\"set_quality\",\"quality\":50,\"scale\":0.5,\"fps\":5}"));
            Assert.StartsWith("VISION_QUALITY:", Tray().Last(), StringComparison.Ordinal);
            // Pano: kullanıcının kabul ettiği oturumda; 1100 yalnızca uzunlukla
            await _worker.HandleVisionControlAsync(Json("{\"action\":\"clipboard\",\"text\":\"merhaba\"}"));
            Assert.Equal("CLIPBOARD_SET:" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("merhaba")), Tray().Last());
            Assert.Equal(new[] { 1010, 1100 }, AuditIds());
            // Başka bir bilgisayara ait denetim yok sayılır; geçersiz denetim tepsiye gitmez
            int count = Tray().Count;
            await _worker.HandleVisionControlAsync(Json("{\"action\":\"select_monitor\",\"index\":0,\"device\":\"HW-BASKA\"}"));
            await _worker.HandleVisionControlAsync(Json("{\"action\":\"teleport\"}"));
            Assert.Equal(count, Tray().Count);
            Assert.Empty(_sent);

            // Oturum bitti: denetim onay ister
            await Server("{\"action\":\"stop_stream\"}");
            await _worker.HandleVisionControlAsync(Json("{\"action\":\"select_monitor\",\"index\":1}"));
            Assert.Equal(count, Tray().Count);
            JsonElement denied = Assert.Single(Denied());
            Assert.Equal("consent", denied.GetProperty("capability").GetString());
            Assert.Equal("select_monitor", denied.GetProperty("action").GetString());
        }

        [Fact]
        public async Task MandatorySession_GetsNoClipboard()
        {
            _worker.OnCommandSocketOpened();
            _worker.Handshake.OnServerInfo(Json("{\"action\":\"server_info\",\"features\":[\"vision_binary\"]}"));
            await Server("{\"action\":\"start_vision_session\",\"session_id\":\"SES-0123456789AB\",\"is_mandatory\":true,\"requested_by\":\"Pasha\"}");
            await _worker.StartVisionFromTrayAsync("START_VISION_TUNNEL:2");
            Assert.Contains("user_approved: False", _audit[0].Message);
            Assert.Equal(new[] { "START_CAPTURE_V2:2" }, Tray().Skip(1));

            await _worker.HandleVisionControlAsync(Json("{\"action\":\"clipboard\",\"text\":\"x\"}"));
            Assert.Equal(new[] { "START_CAPTURE_V2:2" }, Tray().Skip(1));
            Assert.Equal(new[] { 1010 }, AuditIds());
        }
    }
}
