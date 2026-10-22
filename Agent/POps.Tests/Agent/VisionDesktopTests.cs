using System;
using POps.Shared;
using Xunit;

namespace POps.Tests.Agent
{
    // Güvenli masaüstü (UAC onayı, kilit, oturum açma ekranı): tepsi o masaüstünü yakalamaz; görüntüleyiciye donmuş
    // son kare yerine bildirim resmi gider, masaüstü geri gelince yakalama tam kareyle sürer (docs/vision.md)
    public class VisionDesktopTests : TestBase
    {
        private static readonly DateTime T0 = new DateTime(2026, 10, 5, 9, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void InputDesktopThatCannotBeOpened_IsNotOwn()
        {
            // Kullanıcı Winlogon masaüstünü açamaz (erişim reddi): güvenli masaüstü etkin
            Assert.False(VisionDesktop.IsOwn(false, null, "Default"));
            Assert.False(VisionDesktop.IsOwn(false, null, null));
        }

        [Fact]
        public void InputDesktopName_IsComparedWithOwnDesktop()
        {
            Assert.True(VisionDesktop.IsOwn(true, "Default", "Default"));
            Assert.True(VisionDesktop.IsOwn(true, "default", "Default"));
            // Erişilebilen ama başka bir masaüstü (ör. başka bir uygulamanın kendi masaüstü) de yakalanamaz
            Assert.False(VisionDesktop.IsOwn(true, "Winlogon", "Default"));
            Assert.False(VisionDesktop.IsOwn(true, "SebDesktop", "Default"));
        }

        [Fact]
        public void UnreadableNames_KeepCapturingAsBefore()
        {
            Assert.True(VisionDesktop.IsOwn(true, null, "Default"));
            Assert.True(VisionDesktop.IsOwn(true, "Default", null));
            Assert.True(VisionDesktop.IsOwn(true, "", ""));
        }

        [Fact]
        public void OwnDesktop_AlwaysCaptures()
        {
            var gate = new VisionDesktopGate();
            for (int i = 0; i < 3; i++)
                Assert.Equal(VisionDesktopStep.Capture, gate.Next(true, needFull: i == 1, T0.AddSeconds(i)));
            Assert.False(gate.Away);
        }

        [Fact]
        public void LeavingTheDesktop_SendsNoticeOnce_ThenRepeatsEveryFiveSeconds()
        {
            var gate = new VisionDesktopGate();
            Assert.Equal(VisionDesktopStep.Capture, gate.Next(true, false, T0));
            Assert.Equal(VisionDesktopStep.Enter, gate.Next(false, false, T0.AddSeconds(1)));
            Assert.True(gate.Away);
            Assert.Equal(VisionDesktopStep.Wait, gate.Next(false, false, T0.AddSeconds(2)));
            Assert.Equal(VisionDesktopStep.Wait, gate.Next(false, false, T0.AddSeconds(5.9)));
            Assert.Equal(VisionDesktopStep.Notice, gate.Next(false, false, T0.AddSeconds(1) + VisionDesktopGate.NoticeRepeat));
            Assert.Equal(VisionDesktopStep.Wait, gate.Next(false, false, T0.AddSeconds(7)));
        }

        [Fact]
        public void FullFrameRequest_WhileAway_RepeatsNoticeAtOnce()
        {
            // v2: servis kare düşürdü ya da görüntüleyici ekran/ölçek değiştirdi
            var gate = new VisionDesktopGate();
            Assert.Equal(VisionDesktopStep.Enter, gate.Next(false, false, T0));
            Assert.Equal(VisionDesktopStep.Notice, gate.Next(false, true, T0.AddMilliseconds(200)));
            Assert.Equal(VisionDesktopStep.Wait, gate.Next(false, false, T0.AddMilliseconds(400)));
        }

        [Fact]
        public void StreamStartedOnTheSecureDesktop_StartsWithNotice()
        {
            var gate = new VisionDesktopGate();
            Assert.Equal(VisionDesktopStep.Enter, gate.Next(false, true, T0));
        }

        [Fact]
        public void Returning_ResumesOnce_ThenCaptures_AndALaterSwitchIsNoticedAgain()
        {
            var gate = new VisionDesktopGate();
            gate.Next(false, false, T0);
            Assert.Equal(VisionDesktopStep.Resume, gate.Next(true, false, T0.AddSeconds(3)));
            Assert.False(gate.Away);
            Assert.Equal(VisionDesktopStep.Capture, gate.Next(true, false, T0.AddSeconds(4)));
            Assert.Equal(VisionDesktopStep.Enter, gate.Next(false, false, T0.AddSeconds(4.5)));
        }

        [Fact]
        public void ClockGoingBack_RepeatsNotice()
        {
            var gate = new VisionDesktopGate();
            gate.Next(false, false, T0);
            Assert.Equal(VisionDesktopStep.Notice, gate.Next(false, false, T0.AddMinutes(-10)));
        }
    }
}
