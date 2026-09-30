using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using POpsAgent;
using Xunit;

namespace POps.Tests.Agent
{
    // "Etkinlik geçmişim": GET /api/activity/agent/{hw_id} yanıtı, 60 sn önbellek, hata mesajları
    public class ActivityHistoryTests : TestBase
    {
        private const string Sample = "{\"device\":\"HW-1\",\"days\":30,\"items\":[" +
            "{\"at\":\"2026-09-27 19:22:38\",\"kind\":\"remote_session\",\"title\":\"Uzaktan izleme oturumu başladı\",\"actor\":\"Pasha\",\"detail\":\"Gerekçe: arıza\"}," +
            "{\"at\":\"2026-09-27 19:20:00\",\"kind\":\"command\",\"title\":\"Uzaktan komut çalıştırıldı\",\"actor\":null,\"detail\":null}]}";

        private DateTime _now = new DateTime(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);
        private int _calls;
        private string _path;

        private ActivityHistory History(Func<(int?, string)> reply) => new ActivityHistory("https://pops.example", () => "HW-1", null)
        {
            UtcNow = () => _now,
            CanReport = () => true,
            Sender = (method, path, payload, what) =>
            {
                _calls++;
                _path = path;
                Assert.Equal(HttpMethod.Get, method);
                return Task.FromResult<(int? Status, string Body)>(reply());
            },
        };

        // ------------------------------------------------------------------ ayrıştırma

        [Fact]
        public void Parse_KeepsOrderAndTextAsGiven()
        {
            ActivityListMessage m = ActivityHistory.Parse(Sample);
            Assert.True(m.Ok);
            Assert.Equal(30, m.Days);
            Assert.Null(m.Message);
            Assert.Equal(2, m.Items.Count);
            ActivityItem first = m.Items[0];
            Assert.Equal(("2026-09-27 19:22:38", "remote_session", "Uzaktan izleme oturumu başladı", "Pasha", "Gerekçe: arıza"),
                (first.At, first.Kind, first.Title, first.Actor, first.Detail));
            Assert.Null(m.Items[1].Actor);
            Assert.Null(m.Items[1].Detail);
        }

        [Fact]
        public void Parse_MissingFieldsAndUnknownKind_AreStillShown()
        {
            ActivityListMessage m = ActivityHistory.Parse("{\"items\":[{\"kind\":\"backup_restore\"},{},{\"title\":\"  \",\"at\":5},\"x\",null]}");
            Assert.Equal(3, m.Items.Count);
            Assert.Equal(("", "backup_restore", "backup_restore"), (m.Items[0].At, m.Items[0].Kind, m.Items[0].Title));
            Assert.Equal(("", "", "İşlem"), (m.Items[1].At, m.Items[1].Kind, m.Items[1].Title));
            Assert.Equal(("", "İşlem"), (m.Items[2].At, m.Items[2].Title));
            Assert.Equal(ActivityHistory.DefaultDays, m.Days);
        }

        [Fact]
        public void Parse_EmptyList_SaysNothingHappened()
        {
            Assert.Equal(ActivityHistory.EmptyMessage, ActivityHistory.Parse("{\"days\":30,\"items\":[]}").Message);
            Assert.Equal("Son 7 günde işlem yok.", ActivityHistory.Parse("{\"days\":7,\"items\":[]}").Message);
        }

        [Theory]
        [InlineData("")]
        [InlineData("[]")]
        [InlineData("{\"items\":{}}")]
        [InlineData("{\"device\":\"HW-1\"}")]
        [InlineData("{not json")]
        public void Parse_Unreadable_IsNull(string body) => Assert.Null(ActivityHistory.Parse(body));

        [Fact]
        public void Parse_AtMost200Items_AndLongOrControlTextIsCleaned()
        {
            string item = JsonSerializer.Serialize(new { at = "t", kind = "command", title = "Başlık\r\nikinci satır‮", detail = new string('a', 5000) });
            ActivityListMessage m = ActivityHistory.Parse("{\"items\":[" + string.Join(",", Enumerable.Repeat(item, 250)) + "]}");
            Assert.Equal(ActivityHistory.MaxItems, m.Items.Count);
            Assert.Equal("Başlık ikinci satır", m.Items[0].Title);
            Assert.Equal(ActivityHistory.MaxDetail + 1, m.Items[0].Detail.Length);
            Assert.EndsWith("…", m.Items[0].Detail);
        }

        // ------------------------------------------------------------------ hata mesajları

        [Theory]
        [InlineData(null, "Sunucuya ulaşılamadı; biraz sonra yeniden deneyin.", false)]
        [InlineData(429, "Çok sık istek; birkaç saniye sonra yeniden deneyin.", true)]
        [InlineData(401, "Bu bilgisayar sunucuya kayıtlı değil; talep gönderilemedi. BT ekibine haber verin.", false)]
        [InlineData(403, "Bu bilgisayar sunucuya kayıtlı değil; talep gönderilemedi. BT ekibine haber verin.", false)]
        [InlineData(404, "Sunucu etkinlik geçmişini henüz desteklemiyor.", false)]
        [InlineData(500, "Etkinlik geçmişi alınamadı (HTTP 500).", false)]
        public void Errors_AreTurkishMessages(int? status, string message, bool busy)
        {
            ActivityListMessage m = ActivityHistory.FromResponse(status, "{\"detail\":\"x\"}");
            Assert.False(m.Ok);
            Assert.Equal((message, busy), (m.Message, m.Busy));
        }

        [Fact]
        public void BadJson_IsUnreadable() =>
            Assert.Equal(ActivityHistory.UnreadableMessage, ActivityHistory.FromResponse(200, "<html>").Message);

        // ------------------------------------------------------------------ önbellek ve sınır

        [Fact]
        public async Task Success_IsCachedFor60Seconds()
        {
            ActivityHistory h = History(() => (200, Sample));
            Assert.True((await h.GetAsync()).Ok);
            Assert.Equal("/api/activity/agent/HW-1", _path);
            _now += TimeSpan.FromSeconds(59);
            ActivityListMessage cached = await h.GetAsync();
            Assert.True(cached.Ok);
            Assert.Equal(2, cached.Items.Count);
            Assert.Equal(1, _calls);
            _now += TimeSpan.FromSeconds(1);
            await h.GetAsync();
            Assert.Equal(2, _calls);
        }

        [Fact]
        public async Task Failure_IsNotCached_ButRequestsAreSpacedBy6Seconds()
        {
            int? status = 500;
            ActivityHistory h = History(() => (status, status == 200 ? Sample : "{}"));
            Assert.False((await h.GetAsync()).Ok);
            _now += TimeSpan.FromSeconds(3);
            ActivityListMessage busy = await h.GetAsync();
            Assert.True(busy.Busy);
            Assert.Equal(1, _calls);
            _now += TimeSpan.FromSeconds(3);
            status = 200;
            Assert.True((await h.GetAsync()).Ok);
            Assert.Equal(2, _calls);
        }

        [Fact]
        public async Task NotEnrolled_NeverCallsTheServer()
        {
            ActivityHistory h = History(() => (200, Sample));
            h.CanReport = () => false;
            ActivityListMessage m = await h.GetAsync();
            Assert.Equal(Helpdesk.NotEnrolledMessage, m.Message);
            Assert.Equal(0, _calls);
        }

        [Fact]
        public async Task ListAsync_SendsTheEncodedResultToTheTray()
        {
            string frame = null;
            ActivityHistory h = new ActivityHistory("https://pops.example", () => "HW-1", value => frame = value)
            {
                UtcNow = () => _now,
                CanReport = () => true,
                Sender = (_, _, _, _) => Task.FromResult<(int? Status, string Body)>((200, Sample)),
            };

            await h.ListAsync();

            Assert.StartsWith("ACTIVITY_LIST_RESULT:", frame);
            string json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(frame.Substring("ACTIVITY_LIST_RESULT:".Length)));
            Assert.True(JsonSerializer.Deserialize<ActivityListMessage>(json).Ok);
        }
    }
}
