using System;
using System.Linq;
using POps.Shared;
using Xunit;

namespace POps.Tests.Agent
{
    // Uzaktan klavye: tarayıcının key/code değerleri -> SendInput (sanal tuş ya da KEYEVENTF_UNICODE).
    // Eskiden İ (U+0130) 0x30 ("0") oluyordu; ş/ğ/@/€ yanlış ya da hiç yazılmıyordu.
    public class RemoteKeyMapTests : TestBase
    {
        private static readonly RemoteKeyModifiers None = default;
        private static RemoteKeyModifiers Ctrl => new RemoteKeyModifiers { Ctrl = true };
        private static RemoteKeyModifiers AltGr => new RemoteKeyModifiers { Ctrl = true, Alt = true, AltGr = true };

        private static void AssertUnicode(RemoteKeyStroke s, params int[] units)
        {
            Assert.NotNull(s);
            Assert.True(s.IsUnicode, s.ToString());
            Assert.Equal(units, s.Units.Select(u => (int)u).ToArray());
        }

        private static void AssertKey(RemoteKeyStroke s, int vk, bool extended = false)
        {
            Assert.NotNull(s);
            Assert.False(s.IsUnicode, s?.ToString());
            Assert.Equal((vk, extended), (s.VirtualKey, s.Extended));
        }

        // ------------------------------------------------------------------ Türkçe karakterler: Unicode

        [Theory]
        [InlineData("İ", "Quote", true, 0x0130)]
        [InlineData("ı", "KeyI", false, 0x0131)]
        [InlineData("ş", "Semicolon", false, 0x015F)]
        [InlineData("Ğ", "BracketLeft", true, 0x011E)]
        [InlineData("ç", "Period", false, 0x00E7)]
        [InlineData("Ö", "Comma", true, 0x00D6)]
        public void TurkishLetters_AreTypedAsUnicode(string key, string code, bool shift, int unit) =>
            AssertUnicode(RemoteKeyMap.Map(key, code, new RemoteKeyModifiers { Shift = shift }), unit);

        [Fact]
        public void DottedCapitalI_IsNoLongerTheDigitZero()
        {
            RemoteKeyStroke s = RemoteKeyMap.Map("İ", "Quote", new RemoteKeyModifiers { Shift = true });
            Assert.NotEqual(0x30, s.VirtualKey);
            AssertUnicode(s, 0x0130);
        }

        [Theory]
        [InlineData("@", "KeyQ", 0x0040)]
        [InlineData("€", "KeyE", 0x20AC)]
        [InlineData("{", "Digit7", 0x007B)]
        [InlineData("\\", "Minus", 0x005C)]
        public void AltGrCharacters_AreUnicode_NotCtrlAltShortcuts(string key, string code, int unit) =>
            AssertUnicode(RemoteKeyMap.Map(key, code, AltGr), unit);

        [Fact]
        public void Emoji_IsASurrogatePair_TwoUnits() =>
            AssertUnicode(RemoteKeyMap.Map("😀", "", None), 0xD83D, 0xDE00);

        [Fact]
        public void PlainAsciiWithCode_IsUnicodeToo() =>
            AssertUnicode(RemoteKeyMap.Map("a", "KeyA", None), 'a');

        // ------------------------------------------------------------------ kısayollar: sanal tuş

        [Fact]
        public void CtrlC_IsTheVirtualKeyC() => AssertKey(RemoteKeyMap.Map("c", "KeyC", Ctrl), 0x43);

        [Fact]
        public void WinR_IsTheVirtualKeyR() => AssertKey(RemoteKeyMap.Map("r", "KeyR", new RemoteKeyModifiers { Meta = true }), 0x52);

        [Fact]
        public void AltDigit_IsTheDigitKey() => AssertKey(RemoteKeyMap.Map("1", "Digit1", new RemoteKeyModifiers { Alt = true }), 0x31);

        [Fact]
        public void ShortcutOnAPunctuationKey_UsesTheForegroundLayout()
        {
            char asked = '\0';
            RemoteKeyStroke s = RemoteKeyMap.Map("ç", "Period", Ctrl, c => { asked = c; return 0x01DE; });
            Assert.Equal('ç', asked);
            AssertKey(s, 0xDE);
        }

        [Fact]
        public void ShortcutWithoutALayoutKey_FallsBackToUnicode() =>
            AssertUnicode(RemoteKeyMap.Map("ş", "Semicolon", Ctrl, _ => -1), 0x015F);

        // Ctrl ve Alt birlikte (AltGr bildirilmese de) kısayol değildir: Windows AltGr'yi Ctrl+Alt olarak taklit eder
        [Fact]
        public void CtrlAndAltTogether_IsNotAShortcut() =>
            AssertUnicode(RemoteKeyMap.Map("q", "KeyQ", new RemoteKeyModifiers { Ctrl = true, Alt = true }), 'q');

        // ------------------------------------------------------------------ adlı tuşlar

        [Theory]
        [InlineData("F5", 0x74, false)]
        [InlineData("F1", 0x70, false)]
        [InlineData("F24", 0x87, false)]
        [InlineData("ArrowLeft", 0x25, true)]
        [InlineData("ArrowDown", 0x28, true)]
        [InlineData("Home", 0x24, true)]
        [InlineData("End", 0x23, true)]
        [InlineData("PageUp", 0x21, true)]
        [InlineData("PageDown", 0x22, true)]
        [InlineData("Insert", 0x2D, true)]
        [InlineData("Delete", 0x2E, true)]
        [InlineData("Backspace", 0x08, false)]
        [InlineData("Escape", 0x1B, false)]
        [InlineData("Tab", 0x09, false)]
        [InlineData("CapsLock", 0x14, false)]
        [InlineData("NumLock", 0x90, true)]
        [InlineData("ScrollLock", 0x91, false)]
        [InlineData("PrintScreen", 0x2C, true)]
        [InlineData("Pause", 0x13, false)]
        [InlineData("ContextMenu", 0x5D, true)]
        [InlineData("AltGraph", 0xA5, true)]
        public void NamedKeys(string key, int vk, bool extended) => AssertKey(RemoteKeyMap.Map(key, "x", None), vk, extended);

        [Theory]
        [InlineData("Shift", "ShiftLeft", 0xA0, false)]
        [InlineData("Shift", "ShiftRight", 0xA1, false)]
        [InlineData("Control", "ControlLeft", 0xA2, false)]
        [InlineData("Control", "ControlRight", 0xA3, true)]
        [InlineData("Alt", "AltLeft", 0xA4, false)]
        [InlineData("Alt", "AltRight", 0xA5, true)]
        [InlineData("Meta", "MetaLeft", 0x5B, true)]
        [InlineData("Meta", "MetaRight", 0x5C, true)]
        [InlineData("Shift", null, 0x10, false)]
        [InlineData("Control", null, 0x11, false)]
        [InlineData("Enter", "Enter", 0x0D, false)]
        [InlineData("Enter", "NumpadEnter", 0x0D, true)]
        public void Modifiers_LeftAndRightFromCode(string key, string code, int vk, bool extended) =>
            AssertKey(RemoteKeyMap.Map(key, code, None), vk, extended);

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("Dead")]
        [InlineData("Unidentified")]
        [InlineData("Process")]
        [InlineData("\n")]
        public void NothingToSend(string key) => Assert.Null(RemoteKeyMap.Map(key, "KeyA", None));

        // ------------------------------------------------------------------ eski panel (code yok)

        [Theory]
        [InlineData("a", 0x41)]
        [InlineData("Z", 0x5A)]
        [InlineData("7", 0x37)]
        [InlineData(" ", 0x20)]
        public void LegacyMessage_AsciiKeepsTheOldVirtualKeyPath(string key, int vk) => AssertKey(RemoteKeyMap.Map(key, null, Ctrl), vk);

        [Fact]
        public void LegacyMessage_OtherCharactersAreUnicode()
        {
            AssertUnicode(RemoteKeyMap.Map("ş", null, None), 0x015F);
            AssertUnicode(RemoteKeyMap.Map("@", null, None), '@');
            AssertKey(RemoteKeyMap.Map("Enter", null, None), 0x0D);
        }

        [Theory]
        [InlineData("KeyA", 0x41)]
        [InlineData("KeyZ", 0x5A)]
        [InlineData("Digit0", 0x30)]
        [InlineData("Digit9", 0x39)]
        [InlineData("Space", 0x20)]
        [InlineData("Keya", 0)]
        [InlineData("Numpad1", 0)]
        [InlineData(null, 0)]
        public void CodeVirtualKey(string code, int vk) => Assert.Equal(vk, RemoteKeyMap.CodeVirtualKey(code));

        // ------------------------------------------------------------------ basılı kalan tuşlar

        [Fact]
        public void PressedKeys_AreReleasedWhenControlEnds()
        {
            var pressed = new PressedKeys();
            RemoteKeyStroke ctrl = RemoteKeyMap.Map("Control", "ControlRight", None);
            RemoteKeyStroke shift = RemoteKeyMap.Map("Shift", "ShiftLeft", None);
            pressed.Track(ctrl, true);
            pressed.Track(shift, true);
            pressed.Track(RemoteKeyMap.Map("ş", "Semicolon", None), true);   // Unicode takılı kalmaz
            pressed.Track(shift, false);
            Assert.Equal(1, pressed.Count);

            var released = pressed.TakeAll();
            Assert.Single(released);
            AssertKey(released[0], 0xA3, true);
            Assert.Empty(pressed.TakeAll());
        }

        [Fact]
        public void Stroke_DescribesItself()
        {
            Assert.Equal("VK 0x25 ext", RemoteKeyMap.Map("ArrowLeft", "ArrowLeft", None).ToString());
            Assert.Equal("U+0130", RemoteKeyMap.Map("İ", "Quote", None).ToString());
        }

        // ------------------------------------------------------------------ watchdog: aynı hata en fazla 10 dk'da bir

        [Fact]
        public void LogThrottle_SameMessageAtMostOncePerInterval()
        {
            var throttle = new LogThrottle(TimeSpan.FromMinutes(10));
            var t = new DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc);
            Assert.True(throttle.ShouldLog("servis başlatılamadı", t));
            Assert.False(throttle.ShouldLog("servis başlatılamadı", t.AddMinutes(9)));
            Assert.True(throttle.ShouldLog("başka hata", t.AddMinutes(9)));
            Assert.True(throttle.ShouldLog("servis başlatılamadı", t.AddMinutes(10)));
            Assert.True(throttle.ShouldLog(null, t));
        }
    }
}
