using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

#nullable disable

namespace POpsAgent
{
    // Vision v2, servis tarafı (sunucu server_info'da "vision_binary" duyurursa; bkz. POps.Shared.VisionFrame).
    //  * Tepsinin ikili kareleri doğrulanıp tünelden ikili mesaj olarak gider. Geri basınç: önceki kare hâlâ
    //    gönderiliyorsa yeni kare kuyruğa alınmaz, düşürülür; tepsiye VISION_DROPPED gider (kaliteyi düşürür, sonraki
    //    kareyi tam gönderir). Tam kare gelene kadar bölge kareleri iletilmez (görüntüleyici tutarsız kalmasın).
    //  * Görüntüleyicinin denetim mesajları (select_monitor, set_quality) doğrulanıp tepsiye iletilir.
    //  * Pano: yalnızca metin, en çok 64 KB, yalnızca kullanıcının kabul ettiği oturumda, iki yönde. Yerel denetim
    //    kaydına yalnızca yön ve uzunluk yazılır, içerik asla.
    internal sealed class VisionRelay
    {
        public const string BinaryFeature = "vision_binary";
        public const int MaxClipboardBytes = 64 * 1024;
        private static readonly TimeSpan DropNoticeInterval = TimeSpan.FromSeconds(1);

        private readonly Func<ReadOnlyMemory<byte>, Task<bool>> _sendBinary;
        private readonly Func<object, Task<bool>> _sendText;
        private readonly Action<string> _toTray;
        private readonly Func<DateTime> _now;
        private int _sending;
        private volatile bool _needFullFrame;
        private DateTime _lastDropNotice = DateTime.MinValue;
        private bool _badFrameLogged;

        public VisionRelay(Func<ReadOnlyMemory<byte>, Task<bool>> sendBinary, Func<object, Task<bool>> sendText, Action<string> toTray, Func<DateTime> now = null)
        {
            _sendBinary = sendBinary;
            _sendText = sendText;
            _toTray = toTray;
            _now = now ?? (() => DateTime.UtcNow);
        }

        public int Dropped { get; private set; }

        // Yeni oturum: ilk kare tam olmalı
        public void Reset()
        {
            _needFullFrame = true;
            Dropped = 0;
        }

        // Tepsiden gelen işaretli kare (PipeMagic + kare). Dönen: tünele yazıldı mı.
        public async Task<bool> ForwardFrameAsync(byte[] pipeData)
        {
            if (pipeData == null || !POps.Shared.VisionFrame.HasPipeMagic(pipeData)) return false;
            ReadOnlyMemory<byte> frame = pipeData.AsMemory(POps.Shared.VisionFrame.PipeMagic.Length);
            if (!POps.Shared.VisionFrame.TryParse(frame.Span, out var header))
            {
                if (!_badFrameLogged) POpsHelpers.Log("VISION", $"Tepsiden geçersiz Vision karesi geldi ({pipeData.Length} bayt); iletilmedi.", true);
                _badFrameLogged = true;
                return false;
            }
            if (_needFullFrame && header.Kind == POps.Shared.VisionFrame.Region) return false;
            if (Interlocked.CompareExchange(ref _sending, 1, 0) != 0)
            {
                Dropped++;
                if (header.Kind != POps.Shared.VisionFrame.Cursor) _needFullFrame = true;
                DateTime now = _now();
                if (now - _lastDropNotice >= DropNoticeInterval)
                {
                    _lastDropNotice = now;
                    _toTray("VISION_DROPPED");
                }
                return false;
            }
            try
            {
                bool sent = await _sendBinary(frame);
                if (sent && header.Kind == POps.Shared.VisionFrame.Full) _needFullFrame = false;
                return sent;
            }
            finally { Volatile.Write(ref _sending, 0); }
        }

        // Görüntüleyiciden gelen denetim mesajı; tanınmazsa null. userAccepted: kullanıcı oturumu kabul etti (pano için).
        public static string TrayMessageFor(JsonElement root, bool userAccepted, out LocalAuditEvent audit)
        {
            audit = null;
            string action = Text(root, "action");
            switch (action)
            {
                case "select_monitor":
                {
                    string index = root.TryGetProperty("index", out JsonElement i)
                        ? i.ValueKind == JsonValueKind.Number ? i.GetRawText() : i.ValueKind == JsonValueKind.String ? i.GetString() : null
                        : null;
                    return POps.Shared.VisionFrame.TryParseMonitor(index, out byte monitor)
                        ? "VISION_SELECT:" + POps.Shared.VisionFrame.MonitorText(monitor) : null;
                }
                case "set_quality":
                {
                    var q = new POps.Shared.VisionQuality();
                    q.SetTarget(Int(root, "quality") ?? POps.Shared.VisionQuality.DefaultQuality,
                        Number(root, "scale") ?? POps.Shared.VisionQuality.DefaultScale,
                        Int(root, "fps") ?? POps.Shared.VisionQuality.DefaultFps);
                    return "VISION_QUALITY:" + POps.Shared.VisionQuality.Format(q.TargetQuality, q.TargetScale, q.Fps);
                }
                case "clipboard":
                {
                    string text = Text(root, "text");
                    if (!userAccepted || text == null) return null;
                    int bytes = Encoding.UTF8.GetByteCount(text);
                    if (bytes > MaxClipboardBytes) return null;
                    audit = LocalAudit.ClipboardShared("admin_to_pc", text.Length);
                    return "CLIPBOARD_SET:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
                }
                default:
                    return null;
            }
        }

        // Tepsiden "CLIPBOARD:<base64>": kullanıcının kopyaladığı metin görüntüleyiciye
        public async Task<bool> ForwardClipboardAsync(string encoded, bool userAccepted)
        {
            if (!userAccepted) return false;
            string text;
            try
            {
                byte[] bytes = Convert.FromBase64String(encoded ?? "");
                if (bytes.Length == 0 || bytes.Length > MaxClipboardBytes) return false;
                text = Encoding.UTF8.GetString(bytes);
            }
            catch (FormatException) { return false; }
            if (!await _sendText(new Dictionary<string, object> { ["type"] = "clipboard", ["text"] = text })) return false;
            LocalAudit.Write(LocalAudit.ClipboardShared("pc_to_admin", text.Length));
            return true;
        }

        // Tepsiden "VISION_MONITORS:<json dizi>": görüntüleyiciye {"type":"monitors","list":[...]}
        public async Task<bool> ForwardMonitorsAsync(string json)
        {
            List<Dictionary<string, object>> list = ParseMonitors(json);
            return list != null && await _sendText(new Dictionary<string, object> { ["type"] = "monitors", ["list"] = list });
        }

        public static List<Dictionary<string, object>> ParseMonitors(string json)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(json ?? "");
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
                var list = new List<Dictionary<string, object>>();
                foreach (JsonElement m in doc.RootElement.EnumerateArray())
                {
                    int? index = Int(m, "index"), width = Int(m, "width"), height = Int(m, "height");
                    if (index is null or < 0 or >= POps.Shared.VisionFrame.MaxMonitors || width is null or <= 0 or > ushort.MaxValue
                        || height is null or <= 0 or > ushort.MaxValue) return null;
                    bool primary = m.TryGetProperty("primary", out JsonElement p) && p.ValueKind == JsonValueKind.True;
                    list.Add(new Dictionary<string, object> { ["index"] = index.Value, ["width"] = width.Value, ["height"] = height.Value, ["primary"] = primary });
                }
                return list.Count is > 0 and <= POps.Shared.VisionFrame.MaxMonitors ? list : null;
            }
            catch (JsonException) { return null; }
        }

        private static string Text(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static int? Int(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : null;

        private static double? Number(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
    }
}
