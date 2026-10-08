using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using POps.Shared;
using POpsAgent;
using Xunit;

namespace POps.Tests.Agent
{
    // Vision v2, servis: ikili karelerin iletilmesi ve geri basınç, görüntüleyici denetimi, pano
    [Collection(SharedStateCollection.Name)]
    public class VisionRelayTests : SharedStateTestBase
    {
        private static readonly byte[] Jpeg = { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 };
        private readonly List<byte[]> _binary = new List<byte[]>();
        private readonly List<string> _tray = new List<string>();
        private readonly List<Dictionary<string, object>> _text = new List<Dictionary<string, object>>();
        private DateTime _now = new DateTime(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);
        private Func<ReadOnlyMemory<byte>, Task<bool>> _send;

        private VisionRelay Relay()
        {
            _send ??= frame => { lock (_binary) _binary.Add(frame.ToArray()); return Task.FromResult(true); };
            var relay = new VisionRelay(f => _send(f), payload => { lock (_text) _text.Add((Dictionary<string, object>)payload); return Task.FromResult(true); },
                m => { lock (_tray) _tray.Add(m); }, () => _now);
            relay.Reset();
            return relay;
        }

        private static byte[] Frame(byte kind, uint sequence = 1) => VisionFrame.WithPipeMagic(kind switch
        {
            VisionFrame.Full => VisionFrame.Build(kind, 0, sequence, 0, 0, 100, 50, 100, 50, Jpeg),
            VisionFrame.Region => VisionFrame.Build(kind, 0, sequence, 10, 10, 20, 20, 100, 50, Jpeg),
            _ => VisionFrame.Build(kind, 0, sequence, 5, 5, 0, 0, 100, 50, ReadOnlySpan<byte>.Empty),
        });

        [Fact]
        public async Task ValidFrames_GoOutAsBinary_WithoutThePipeMarker()
        {
            VisionRelay relay = Relay();
            byte[] full = Frame(VisionFrame.Full);
            Assert.True(await relay.ForwardFrameAsync(full));
            Assert.Equal(full.Skip(2).ToArray(), Assert.Single(_binary));
            Assert.True(await relay.ForwardFrameAsync(Frame(VisionFrame.Region, 2)));
            Assert.True(await relay.ForwardFrameAsync(Frame(VisionFrame.Cursor, 3)));
            Assert.Equal(3, _binary.Count);
        }

        [Fact]
        public async Task InvalidOrUnmarkedFrames_AreNotForwarded()
        {
            VisionRelay relay = Relay();
            Assert.False(await relay.ForwardFrameAsync(Jpeg));
            Assert.False(await relay.ForwardFrameAsync(VisionFrame.WithPipeMagic(new byte[] { 1, 2, 3 })));
            Assert.Empty(_binary);
        }

        [Fact]
        public async Task SessionStartsWithAFullFrame()
        {
            VisionRelay relay = Relay();
            Assert.False(await relay.ForwardFrameAsync(Frame(VisionFrame.Region)));
            Assert.True(await relay.ForwardFrameAsync(Frame(VisionFrame.Cursor)));
            Assert.True(await relay.ForwardFrameAsync(Frame(VisionFrame.Full)));
            Assert.True(await relay.ForwardFrameAsync(Frame(VisionFrame.Region)));
        }

        // Önceki kare hâlâ gidiyorsa yeni kare kuyruğa alınmaz, düşer; tam kare gelene kadar bölgeler iletilmez
        [Fact]
        public async Task BackPressure_DropsFrames_AsksForAFullFrame_AndLowersQuality()
        {
            var slow = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int calls = 0;
            _send = async frame => { if (Interlocked.Increment(ref calls) == 2) await slow.Task; lock (_binary) _binary.Add(frame.ToArray()); return true; };
            VisionRelay relay = Relay();
            Assert.True(await relay.ForwardFrameAsync(Frame(VisionFrame.Full)));

            Task<bool> stuck = relay.ForwardFrameAsync(Frame(VisionFrame.Region, 2));
            Assert.False(await relay.ForwardFrameAsync(Frame(VisionFrame.Region, 3)));
            Assert.False(await relay.ForwardFrameAsync(Frame(VisionFrame.Full, 4)));
            Assert.Equal(2, relay.Dropped);
            lock (_tray) Assert.Equal(new[] { "VISION_DROPPED" }, _tray);   // saniyede en çok bir kez

            slow.SetResult(true);
            Assert.True(await stuck);
            Assert.False(await relay.ForwardFrameAsync(Frame(VisionFrame.Region, 5)));   // tam kare bekleniyor
            Assert.True(await relay.ForwardFrameAsync(Frame(VisionFrame.Full, 6)));
            Assert.True(await relay.ForwardFrameAsync(Frame(VisionFrame.Region, 7)));

            _now = _now.AddSeconds(2);
            var slow2 = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _send = async frame => { await slow2.Task; return true; };
            Task<bool> stuck2 = relay.ForwardFrameAsync(Frame(VisionFrame.Full, 8));
            Assert.False(await relay.ForwardFrameAsync(Frame(VisionFrame.Region, 9)));
            lock (_tray) Assert.Equal(2, _tray.Count);
            slow2.SetResult(true);
            await stuck2;
        }

        private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

        [Theory]
        [InlineData("{\"action\":\"select_monitor\",\"index\":0}", "VISION_SELECT:0")]
        [InlineData("{\"action\":\"select_monitor\",\"index\":3}", "VISION_SELECT:3")]
        [InlineData("{\"action\":\"select_monitor\",\"index\":\"all\"}", "VISION_SELECT:all")]
        [InlineData("{\"action\":\"select_monitor\",\"index\":99}", null)]
        [InlineData("{\"action\":\"select_monitor\",\"index\":\"x\"}", null)]
        [InlineData("{\"action\":\"set_quality\",\"quality\":50,\"scale\":0.75,\"fps\":8}", "VISION_QUALITY:50,0.75,8")]
        [InlineData("{\"action\":\"set_quality\",\"quality\":200,\"scale\":0.1,\"fps\":0}", "VISION_QUALITY:75,0.5,1")]
        [InlineData("{\"action\":\"set_quality\"}", "VISION_QUALITY:60,1,5")]
        [InlineData("{\"action\":\"start_stream\"}", null)]
        public void ViewerControl_IsValidatedBeforeTheTray(string json, string expected)
        {
            Assert.Equal(expected, VisionRelay.TrayMessageFor(Json(json), userAccepted: false, out LocalAuditEvent audit));
            Assert.Null(audit);
        }

        [Fact]
        public void ClipboardToThePc_OnlyInAnAcceptedSession_AndUpTo64KB()
        {
            string json = "{\"action\":\"clipboard\",\"text\":\"merhaba Ş\"}";
            Assert.Null(VisionRelay.TrayMessageFor(Json(json), userAccepted: false, out _));

            string message = VisionRelay.TrayMessageFor(Json(json), userAccepted: true, out LocalAuditEvent audit);
            Assert.Equal("CLIPBOARD_SET:" + Convert.ToBase64String(Encoding.UTF8.GetBytes("merhaba Ş")), message);
            Assert.Equal(1100, audit.EventId);
            Assert.Contains("direction: admin_to_pc", audit.Message);
            Assert.Contains("length: 9", audit.Message);
            Assert.DoesNotContain("merhaba", audit.Message);

            string big = JsonSerializer.Serialize(new { action = "clipboard", text = new string('a', VisionRelay.MaxClipboardBytes + 1) });
            Assert.Null(VisionRelay.TrayMessageFor(Json(big), userAccepted: true, out _));
        }

        [Fact]
        public async Task ClipboardFromThePc_OnlyInAnAcceptedSession()
        {
            VisionRelay relay = Relay();
            string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("kopya"));
            Assert.False(await relay.ForwardClipboardAsync(encoded, userAccepted: false));
            Assert.False(await relay.ForwardClipboardAsync("%%%", userAccepted: true));
            Assert.False(await relay.ForwardClipboardAsync(Convert.ToBase64String(new byte[VisionRelay.MaxClipboardBytes + 1]), userAccepted: true));
            Assert.Empty(_text);

            Assert.True(await relay.ForwardClipboardAsync(encoded, userAccepted: true));
            var sent = Assert.Single(_text);
            Assert.Equal("clipboard", sent["type"]);
            Assert.Equal("kopya", sent["text"]);
        }

        [Fact]
        public async Task MonitorList_IsValidated()
        {
            VisionRelay relay = Relay();
            Assert.True(await relay.ForwardMonitorsAsync("[{\"index\":0,\"width\":1920,\"height\":1080,\"primary\":true},{\"index\":1,\"width\":1280,\"height\":1024,\"primary\":false}]"));
            var sent = Assert.Single(_text);
            Assert.Equal("monitors", sent["type"]);
            Assert.Equal(2, ((List<Dictionary<string, object>>)sent["list"]).Count);

            Assert.Null(VisionRelay.ParseMonitors("[]"));
            Assert.Null(VisionRelay.ParseMonitors("[{\"index\":0,\"width\":0,\"height\":1080}]"));
            Assert.Null(VisionRelay.ParseMonitors("{\"index\":0}"));
            Assert.Null(VisionRelay.ParseMonitors("bozuk"));
            Assert.Null(VisionRelay.ParseMonitors(JsonSerializer.Serialize(Enumerable.Range(0, 17).Select(i => new { index = i % 16, width = 10, height = 10 }))));
        }

        // Eski sunucu (vision_binary yok): eski tam kare akışı; yeni sunucu: v2
        [Theory]
        [InlineData(false, "START_CAPTURE:4")]
        [InlineData(true, "START_CAPTURE_V2:4")]
        public void StartCapture_PicksTheProtocolFromServerInfo(bool binary, string expected)
        {
            var tray = new List<string>();
            using var worker = new Worker(NullLogger<Worker>.Instance) { TrayOverride = tray.Add, SendOverride = _ => Task.FromResult(true) };
            worker.OnCommandSocketOpened();
            worker.Handshake.OnServerInfo(Json(binary ? "{\"action\":\"server_info\",\"features\":[\"vision_binary\"]}" : "{\"action\":\"server_info\",\"features\":[]}"));
            worker.StartCapture(4);
            Assert.Equal(expected, tray.First());
            Assert.DoesNotContain("CLIPBOARD_SHARE:1", tray);   // kullanıcı kabul etmedi
        }

        [Fact]
        public async Task ViewerControl_WithoutAnApprovedSession_IsDenied()
        {
            var sent = new List<JsonElement>();
            var tray = new List<string>();
            using var worker = new Worker(NullLogger<Worker>.Instance)
            {
                HwId = "HW-V2",
                TrayOverride = tray.Add,
                SendOverride = p => { sent.Add(JsonSerializer.SerializeToElement(p)); return Task.FromResult(true); },
            };
            await worker.HandleVisionControlAsync(Json("{\"action\":\"clipboard\",\"text\":\"x\"}"));
            Assert.Empty(tray);
            JsonElement denied = Assert.Single(sent);
            Assert.Equal("capability_denied", denied.GetProperty("type").GetString());
            Assert.Equal("clipboard", denied.GetProperty("action").GetString());

            await worker.HandleVisionControlAsync(Json("{\"action\":\"select_monitor\",\"index\":0,\"device\":\"HW-BASKA\"}"));
            Assert.Single(sent);
        }
    }
}
