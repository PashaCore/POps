using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using POps.Shared;
using POpsAgent;
using Xunit;

namespace POps.Tests.Agent
{
    // Bilgisayar kilitliyken başlayan Vision oturumu (docs/vision.md "Secure desktop", karar 5): sessiz oturum yok. Tepsi
    // oturumu başlatırken kullanıcının masaüstü ekranda değilse servis 1150 yazar ve tepsiye oturum bildirimini kurdurur;
    // tepsi bildirimi masaüstü geri gelince gösterir ve oturum bitene kadar açık tutar. Gerçek masaüstü, pencere ya da
    // Vision tüneli yok: tepsinin kararları POps.Shared'de saf, servisin tüneli, Olay Günlüğü ve sunucu kaydı sahte.
    [Collection(SharedStateCollection.Name)]
    public class VisionLockedStartTests : SharedStateTestBase, IDisposable
    {
        private const string MandatoryRequest = "{\"action\":\"start_vision_session\",\"session_id\":\"SES-0123456789AB\",\"is_mandatory\":true,"
            + "\"admin_name\":\"Pasha\",\"requested_by\":\"Pasha\",\"reason\":\"Güvenlik güncellemesi\",\"countdown_seconds\":30}";
        private const string ConsentRequest = "{\"action\":\"start_vision_session\",\"session_id\":\"SES-00000000CAFE\",\"is_mandatory\":false,"
            + "\"admin_name\":\"Ayşe\",\"requested_by\":\"Ayşe\",\"reason\":\"Yazıcı sorunu\",\"countdown_seconds\":0}";

        private readonly Worker _worker;
        private readonly List<string> _tray = new List<string>();
        private readonly List<LocalAuditEvent> _audit = new List<LocalAuditEvent>();
        private readonly List<AgentLogPayload> _serverLog = new List<AgentLogPayload>();
        private int _tunnelOpens;
        private bool _tunnelSucceeds = true;

        public VisionLockedStartTests()
        {
            SecureStore.Dir = TestEnvironment.NewDir("vision-locked-secure");
            AgentCapabilities.Load();   // dosya yok: Vision açık
            _worker = new Worker(NullLogger<Worker>.Instance, AgentHarness.FromStatics().Context)
            {
                HwId = "HW-LOCK",
                TrayOverride = m => { lock (_tray) _tray.Add(m); },
                SendOverride = _ => Task.FromResult(true),
                VisionTunnelOverride = () => { _tunnelOpens++; return Task.FromResult(_tunnelSucceeds); },
                AuditOverride = e => { lock (_audit) _audit.Add(e); },
                DeviceLogOverride = log => { lock (_serverLog) _serverLog.Add(log); return Task.CompletedTask; },
            };
        }

        public void Dispose()
        {
            _worker.Dispose();
            SecureStore.Dir = TestEnvironment.NewDir("vision-locked-reset");
            AgentCapabilities.Load();
        }

        private Task Server(string json) => _worker.HandleServerMessageAsync(json, null, CancellationToken.None);

        private int[] AuditIds() => _audit.Select(e => e.EventId).ToArray();

        private List<string> TrayNotices() => _tray.Where(m => m.StartsWith("VISION_NOTICE", StringComparison.Ordinal)).ToList();

        // ------------------------------------------------------------------ tepsi -> servis mesajı

        [Theory]
        [InlineData("START_VISION_TUNNEL", 2, false)]
        [InlineData("START_VISION_TUNNEL:5", 5, false)]
        [InlineData("START_VISION_TUNNEL:x", 2, false)]
        [InlineData("START_VISION_TUNNEL:5:locked", 5, true)]
        [InlineData("START_VISION_TUNNEL:5:LOCKED", 5, false)]
        [InlineData("START_VISION_TUNNEL:5:baska", 5, false)]
        public void TunnelMessage_IsParsed(string message, int fps, bool locked) =>
            Assert.Equal((fps, locked), VisionSessionStart.ParseTunnelMessage(message));

        [Fact]
        public void TunnelMessage_CarriesTheFlagOnlyWhenLocked_AndAnOldServiceStillReadsTheFps()
        {
            Assert.Equal("START_VISION_TUNNEL:2", VisionSessionStart.TunnelMessage(2, lockedAtStart: false));
            string locked = VisionSessionStart.TunnelMessage(5, lockedAtStart: true);
            Assert.Equal("START_VISION_TUNNEL:5:locked", locked);
            // Eski servis: int.TryParse(message.Split(':')[1])
            Assert.Equal("5", locked.Split(':')[1]);
        }

        // ------------------------------------------------------------------ tepsinin kararları (saf)

        [Fact]
        public void ConsentRequest_IsAlwaysAskedEvenWithACountdown_SoNothingStartsOnATimer()
        {
            Assert.Equal(VisionStartPlan.AskUser, VisionSessionStart.Plan(mandatory: false, countdownSeconds: 30));
            Assert.Equal(VisionStartPlan.AskUser, VisionSessionStart.Plan(mandatory: false, countdownSeconds: 0));
            Assert.Equal(VisionStartPlan.Countdown, VisionSessionStart.Plan(mandatory: true, countdownSeconds: 30));
            Assert.Equal(VisionStartPlan.Immediate, VisionSessionStart.Plan(mandatory: true, countdownSeconds: 0));
            Assert.Equal(VisionStartPlan.Immediate, VisionSessionStart.Plan(mandatory: true, countdownSeconds: -1));
        }

        [Fact]
        public void StartedWhileLocked_NoticeWaitsForTheDesktop_HoldsCapture_AndStaysUntilTheEnd()
        {
            var notice = new VisionSessionNotice();
            notice.Arm();
            // Kilit ekranı: gösterilecek yer yok, yakalama da kullanıcının masaüstünü göremez
            Assert.False(notice.Due(ownDesktop: false));
            Assert.True(notice.Waiting);
            Assert.False(notice.Capturable(ownDesktop: false));
            // Kullanıcı döndü: bildirim ekrana konmadan görüntü alınmaz
            Assert.False(notice.Capturable(ownDesktop: true));
            Assert.True(notice.Due(ownDesktop: true));
            Assert.True(notice.MarkShown());
            Assert.True(notice.Capturable(ownDesktop: true));
            // Gösterildi: kilit/açılış tekrarlansa da bir daha "göster" yok, bildirim yerinde kalır
            Assert.False(notice.Due(ownDesktop: true));
            Assert.False(notice.Capturable(ownDesktop: false));
            Assert.False(notice.Due(ownDesktop: true));
            Assert.False(notice.MarkShown());
            Assert.True(notice.Shown);
            Assert.True(notice.Armed);
            // Oturum bitti: ekrandaki bildirim kapatılır
            Assert.True(notice.End());
            Assert.False(notice.Armed);
            Assert.False(notice.Shown);
            Assert.True(notice.Capturable(ownDesktop: true));
        }

        [Fact]
        public void SessionThatEndsBeforeTheUserReturns_HasNothingToClose()
        {
            var notice = new VisionSessionNotice();
            notice.Arm();
            Assert.False(notice.End());
            Assert.False(notice.Due(ownDesktop: true));
        }

        [Fact]
        public void StartedUnlocked_NeverShowsANotice_AndCaptureFollowsTheDesktop()
        {
            var notice = new VisionSessionNotice();
            Assert.False(notice.Due(ownDesktop: true));
            Assert.False(notice.Waiting);
            Assert.True(notice.Capturable(ownDesktop: true));
            Assert.False(notice.Capturable(ownDesktop: false));
            Assert.False(notice.MarkShown());
            Assert.False(notice.End());
        }

        [Fact]
        public void NoticeMessage_RoundTrips_AndABrokenOneStillArmsTheNotice()
        {
            string on = VisionSessionNotice.OnMessage(new VisionNoticeInfo("SES-0123456789AB", "Pasha", "Güvenlik: \"acil\"", true));
            Assert.StartsWith(VisionSessionNotice.OnPrefix, on);
            Assert.Equal(new VisionNoticeInfo("SES-0123456789AB", "Pasha", "Güvenlik: \"acil\"", true), VisionSessionNotice.ParseOn(on));
            Assert.Equal(new VisionNoticeInfo(null, null, null, false), VisionSessionNotice.ParseOn(VisionSessionNotice.OnMessage(new VisionNoticeInfo(null, null, null, false))));
            // Bozuk içerik: ayrıntısız da olsa bildirim kurulur (sessiz oturum olmasın)
            Assert.Equal(new VisionNoticeInfo(null, null, null, false), VisionSessionNotice.ParseOn(VisionSessionNotice.OnPrefix + "%%%"));
            Assert.Equal(new VisionNoticeInfo(null, null, null, false), VisionSessionNotice.ParseOn(VisionSessionNotice.OnPrefix + Convert.ToBase64String(new byte[] { 0x5B, 0x31, 0x5D })));
            Assert.Null(VisionSessionNotice.ParseOn(VisionSessionNotice.Off));
            Assert.Null(VisionSessionNotice.ParseOn("START_CAPTURE:2"));
        }

        // ------------------------------------------------------------------ servis

        [Fact]
        public async Task MandatorySessionStartedWhileLocked_Writes1150Once_AndArmsTheNoticeUntilTheSessionEnds()
        {
            await Server(MandatoryRequest);
            Assert.Equal(MandatoryRequest, Assert.Single(_tray));   // geri sayım tepside
            Assert.Equal(0, _tunnelOpens);
            Assert.Empty(_audit);

            await _worker.StartVisionFromTrayAsync("START_VISION_TUNNEL:2:locked");

            Assert.Equal(new[] { 1010, 1150 }, AuditIds());
            LocalAuditEvent locked = _audit[1];
            Assert.Equal(LocalAuditLevel.Warning, locked.Level);
            Assert.StartsWith("Vision oturumu bilgisayar kilitliyken başladı", locked.Message, StringComparison.Ordinal);
            Assert.Contains("session_id: SES-0123456789AB", locked.Message);
            Assert.Contains("requested_by: Pasha", locked.Message);
            Assert.Contains("mandatory: True", locked.Message);
            Assert.DoesNotContain("Güvenlik güncellemesi", locked.Message);   // gerekçe ve ekran içeriği Olay Günlüğüne girmez

            // Bildirim yakalamadan önce kurulur (tepsi kullanıcının masaüstünü ancak bildirim ekrandayken yakalar)
            int noticeAt = _tray.FindIndex(m => m.StartsWith(VisionSessionNotice.OnPrefix, StringComparison.Ordinal));
            int captureAt = _tray.IndexOf("START_CAPTURE:2");
            Assert.True(noticeAt > 0 && captureAt > noticeAt, string.Join(" | ", _tray));
            Assert.Equal(new VisionNoticeInfo("SES-0123456789AB", "Pasha", "Güvenlik güncellemesi", true), VisionSessionNotice.ParseOn(_tray[noticeAt]));

            // Sunucunun olay günlüğüne sıradan bir kayıt (mevcut POST /api/logs; protokol değişmedi)
            AgentLogPayload log = Assert.Single(_serverLog);
            Assert.Equal("agent.vision_locked_start", log.EventType);
            Assert.Equal("vision", log.Category);
            Assert.Equal("SES-0123456789AB", log.MetaData["session_id"]);
            Assert.Equal("Pasha", log.MetaData["requested_by"]);
            Assert.Equal(true, log.MetaData["mandatory"]);

            // Oturum sürerken bildirim kapanmaz; kilitsiz yeniden başlatma yeni 1150 yazmaz, bildirimi de kapatmaz
            await _worker.StartVisionFromTrayAsync("START_VISION_TUNNEL:2");
            Assert.Single(TrayNotices());
            Assert.Equal(1, AuditIds().Count(id => id == 1150));

            // Oturum bitti: 1011 ve bildirim kapanır
            await Server("{\"action\":\"stop_stream\"}");
            Assert.Equal(new[] { 1010, 1150, 1011 }, AuditIds());
            Assert.Equal(VisionSessionNotice.Off, TrayNotices().Last());
            Assert.Equal(2, TrayNotices().Count);

            // Bitmiş oturum ikinci kez kapatılmaz
            await Server("{\"action\":\"stop_stream\"}");
            Assert.Equal(2, TrayNotices().Count);
            Assert.Single(_serverLog);
        }

        [Fact]
        public async Task SessionStartedUnlocked_HasNo1150_AndNoNotice()
        {
            await Server(MandatoryRequest);
            await _worker.StartVisionFromTrayAsync("START_VISION_TUNNEL:2");
            Assert.Equal(new[] { 1010 }, AuditIds());
            Assert.Contains("START_CAPTURE:2", _tray);
            Assert.Empty(TrayNotices());
            Assert.Empty(_serverLog);

            await Server("{\"action\":\"stop_stream\"}");
            Assert.Equal(new[] { 1010, 1011 }, AuditIds());
            Assert.Empty(TrayNotices());
        }

        [Fact]
        public async Task ConsentRequestWhileLocked_StartsNothingOnItsOwn()
        {
            // Onay penceresi tepside kullanıcının masaüstünde bekler; servis kendiliğinden tünel açmaz, yakalatmaz
            await Server(ConsentRequest);
            Assert.Equal(ConsentRequest, Assert.Single(_tray));
            Assert.Equal(0, _tunnelOpens);
            Assert.Empty(_audit);
            Assert.Empty(_serverLog);

            // Kuramsal: kullanıcı "Evet" dedikten hemen sonra masaüstü gitti. Yine sessiz değil: 1150 (zorunlu değil) ve bildirim
            await _worker.StartVisionFromTrayAsync("START_VISION_TUNNEL:2:locked");
            Assert.Equal(new[] { 1010, 1150 }, AuditIds());
            Assert.Contains("user_approved: True", _audit[0].Message);
            Assert.Contains("mandatory: False", _audit[1].Message);
            Assert.False(VisionSessionNotice.ParseOn(Assert.Single(TrayNotices())).Mandatory);
        }

        [Fact]
        public async Task TunnelThatDoesNotOpen_WritesNothingAndArmsNothing()
        {
            _tunnelSucceeds = false;
            await Server(MandatoryRequest);
            await _worker.StartVisionFromTrayAsync("START_VISION_TUNNEL:2:locked");
            Assert.Equal(1, _tunnelOpens);
            Assert.Empty(_audit);
            Assert.Empty(TrayNotices());
            Assert.DoesNotContain("START_CAPTURE:2", _tray);
            Assert.Empty(_serverLog);
        }

        [Fact]
        public async Task SessionEndedByALostServerConnection_ClosesTheNotice()
        {
            await Server(MandatoryRequest);
            await _worker.StartVisionFromTrayAsync("START_VISION_TUNNEL:2:locked");
            await _worker.OnCommandConnectionLostAsync();
            Assert.Equal(new[] { 1010, 1150, 1011 }, AuditIds());
            Assert.Equal(VisionSessionNotice.Off, TrayNotices().Last());
        }

        [Fact]
        public void AuditEvent1150_HasTheContractIdAndFields()
        {
            LocalAuditEvent e = LocalAudit.VisionStartedWhileLocked("SES-0123456789AB", "Pasha\r\nsahte", true);
            Assert.Equal(1150, e.EventId);
            Assert.Equal(LocalAuditLevel.Warning, e.Level);
            Assert.Contains("requested_by: Pasha??sahte", e.Message);
            Assert.Contains("session_id: (yok)", LocalAudit.VisionStartedWhileLocked(null, null, false).Message);
        }
    }
}
