using System.Globalization;
using System.Text.Json;

namespace POps.Shared
{
    // Vision oturumu bilgisayar kilitliyken başladı (karar: docs/vision.md "Secure desktop", karar 5). Zorunlu oturumun
    // geri sayımı kullanıcının masaüstündeki bir penceredir; kilit, oturum açma, UAC ya da Ctrl+Alt+Del ekranı açıkken o
    // masaüstü görünmez, geri sayım biter ve oturum kullanıcı hiçbir şey görmeden başlardı. Artık sessiz oturum yok:
    // tepsi oturumu başlatırken masaüstünün görünüp görünmediğini servise söyler; servis Olay Günlüğüne 1150 yazar ve
    // tepsiye oturum bildirimini kurdurur; tepsi kullanıcının masaüstü geri gelir gelmez bildirimi gösterir ve oturum
    // bitene kadar açık tutar.

    public enum VisionStartPlan
    {
        // Onay istenir: oturum yalnızca kullanıcı "Evet" derse başlar
        AskUser,
        // Zorunlu: geri sayım, bitince başlar
        Countdown,
        // Zorunlu, geri sayımsız: bildirim ve hemen başlar
        Immediate,
    }

    public static class VisionSessionStart
    {
        public const string TunnelPrefix = "START_VISION_TUNNEL";
        public const string LockedFlag = "locked";
        public const int DefaultFps = 2;

        // Zorunlu olmayan istek her zaman kullanıcıya sorulur: ne süre ne de masaüstünün geri gelmesi onu kendiliğinden
        // başlatır. Bilgisayar kilitliyken gelen onay penceresi kullanıcının masaüstünde yanıt bekler.
        public static VisionStartPlan Plan(bool mandatory, int countdownSeconds) =>
            !mandatory ? VisionStartPlan.AskUser
            : countdownSeconds > 0 ? VisionStartPlan.Countdown
            : VisionStartPlan.Immediate;

        // Tepsi -> servis: "START_VISION_TUNNEL:fps"; kullanıcının masaüstü o an ekranda değilse sonuna ":locked". Eski
        // servis yalnızca ikinci alanı (fps) okur, eski tepsi bayrağı hiç göndermez.
        public static string TunnelMessage(int fps, bool lockedAtStart) =>
            TunnelPrefix + ":" + fps.ToString(CultureInfo.InvariantCulture) + (lockedAtStart ? ":" + LockedFlag : "");

        // fps okunamazsa 2 (eskisi gibi); bayrak yalnızca tam olarak "locked"
        public static (int Fps, bool LockedAtStart) ParseTunnelMessage(string message)
        {
            ArgumentNullException.ThrowIfNull(message);
            string[] parts = message.Split(':');
            int fps = parts.Length > 1 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : DefaultFps;
            return (fps, parts.Length > 2 && parts[2] == LockedFlag);
        }
    }

    // Bildirimin ayrıntıları (servisin start_vision_session'dan bildikleri); hiçbiri zorunlu değil
    public sealed record VisionNoticeInfo(string? SessionId, string? RequestedBy, string? Reason, bool Mandatory);

    // Tepsi tarafı (saf, iş parçacığı güvenli). Servis bildirimi kurar (Arm); kullanıcının masaüstü görününce tepsi
    // bildirimi gösterir (Due, MarkShown); oturum bitince kapatır (End). Bildirim ekrana gelene kadar yakalama bekletilir:
    // kullanıcının masaüstünden, üstünde bildirim olmadan görüntü gitmez (o arada görüntüleyiciye güvenli masaüstü
    // bildirim resmi gider). Gösterilen bildirim, masaüstü yeniden gidip gelse de oturum sonuna kadar kalır.
    public sealed class VisionSessionNotice
    {
        // Servis -> tepsi: VISION_NOTICE_ON:<base64 JSON {"session_id","requested_by","reason","mandatory"}>, VISION_NOTICE_OFF
        public const string OnPrefix = "VISION_NOTICE_ON:";
        public const string Off = "VISION_NOTICE_OFF";

        private readonly object _gate = new object();
        private bool _armed;
        private bool _shown;

        public bool Armed { get { lock (_gate) return _armed; } }

        public bool Shown { get { lock (_gate) return _shown; } }

        // Kurulmuş, henüz ekranda değil: kullanıcının masaüstü geri gelince gösterilecek
        public bool Waiting { get { lock (_gate) return _armed && !_shown; } }

        public void Arm()
        {
            lock (_gate) _armed = true;
        }

        // Bildirim şimdi gösterilmeli mi: kurulmuş, henüz gösterilmemiş ve kullanıcının masaüstü ekranda
        public bool Due(bool ownDesktop)
        {
            lock (_gate) return _armed && !_shown && ownDesktop;
        }

        // Bildirim ekrana kondu: yakalama sürebilir. Dönen: ilk gösterim mi
        public bool MarkShown()
        {
            lock (_gate)
            {
                if (!_armed || _shown) return false;
                _shown = true;
                return true;
            }
        }

        // Yakalama döngüleri her kareden önce sorar: masaüstü görünüyor ve bildirim beklenmiyor
        public bool Capturable(bool ownDesktop) => ownDesktop && !Waiting;

        // Oturum bitti. Dönen: bildirim ekrandaydı (kapatılmalı)
        public bool End()
        {
            lock (_gate)
            {
                bool shown = _shown;
                _armed = false;
                _shown = false;
                return shown;
            }
        }

        public static string OnMessage(VisionNoticeInfo info)
        {
            ArgumentNullException.ThrowIfNull(info);
            using var buffer = new MemoryStream();
            using (var json = new Utf8JsonWriter(buffer))
            {
                json.WriteStartObject();
                json.WriteString("session_id", info.SessionId);
                json.WriteString("requested_by", info.RequestedBy);
                json.WriteString("reason", info.Reason);
                json.WriteBoolean("mandatory", info.Mandatory);
                json.WriteEndObject();
            }
            return OnPrefix + Convert.ToBase64String(buffer.ToArray());
        }

        // null: VISION_NOTICE_ON değil. İçerik bozuksa bildirim yine kurulur (sessiz oturum olmasın), yalnızca ayrıntısız.
        public static VisionNoticeInfo? ParseOn(string message)
        {
            ArgumentNullException.ThrowIfNull(message);
            if (!message.StartsWith(OnPrefix, StringComparison.Ordinal)) return null;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(Convert.FromBase64String(message.Substring(OnPrefix.Length)));
                JsonElement root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return new VisionNoticeInfo(null, null, null, false);
                return new VisionNoticeInfo(Text(root, "session_id"), Text(root, "requested_by"), Text(root, "reason"),
                    root.TryGetProperty("mandatory", out JsonElement mandatory) && mandatory.ValueKind == JsonValueKind.True);
            }
            catch (FormatException) { return new VisionNoticeInfo(null, null, null, false); }
            catch (JsonException) { return new VisionNoticeInfo(null, null, null, false); }
        }

        private static string? Text(JsonElement root, string name) =>
            root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
}
