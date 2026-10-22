using System;
using POpsAgent;
using Xunit;

namespace POps.Tests.Agent
{
    public class SessionEventsTests : TestBase
    {
        private static readonly DateTime Boot = new DateTime(2026, 9, 28, 7, 50, 0, DateTimeKind.Utc);

        private static SessionSnapshot S(string user, int id = 1, DateTime? boot = null) =>
            new SessionSnapshot { User = user, SessionId = id, BootUtc = boot ?? Boot };

        [Fact]
        public void SameLogon_NothingToReport()
        {
            Assert.Empty(SessionEvents.Diff(S("ogrenci"), S("OGRENCI", 1, Boot.AddSeconds(3))));
            Assert.Empty(SessionEvents.Diff(S(null), S(null)));
        }

        [Fact]
        public void FirstLogon_IsLogin() =>
            Assert.Equal(new[] { ("login", "ogrenci") }, SessionEvents.Diff(null, S("ogrenci")));

        [Fact]
        public void SignOut_IsLogout() =>
            Assert.Equal(new[] { ("logout", "ogrenci") }, SessionEvents.Diff(S("ogrenci"), S(null)));

        [Fact]
        public void UserSwitch_LogoutThenLogin() =>
            Assert.Equal(new[] { ("logout", "ali"), ("login", "ayse") }, SessionEvents.Diff(S("ali"), S("ayse", 2)));

        [Fact]
        public void SameUserNewSession_IsANewLogon() =>
            Assert.Equal(new[] { ("logout", "ali"), ("login", "ali") }, SessionEvents.Diff(S("ali", 1), S("ali", 3)));

        [Fact]
        public void AfterReboot_SameSessionNumberIsANewLogon() =>
            Assert.Equal(new[] { ("logout", "ali"), ("login", "ali") }, SessionEvents.Diff(S("ali", 1), S("ali", 1, Boot.AddHours(20))));

        [Fact]
        public void ReportedStateSurvivesRestart()
        {
            string statePath = AgentHarness.Create("session").Paths.DataFile(SessionReporter.StateFileName);
            if (System.IO.File.Exists(statePath)) System.IO.File.Delete(statePath);
            Assert.Null(SessionReporter.Load(statePath));
            SessionReporter.Save(statePath, S("ali", 2));
            Assert.Empty(SessionEvents.Diff(SessionReporter.Load(statePath), S("ali", 2)));
            if (System.IO.File.Exists(statePath)) System.IO.File.Delete(statePath);
        }
    }

    // Ön plan uygulaması: yalnızca süreç adı kabul edilir (KVKK: pencere başlığı gönderilmez)
    public class ActiveAppTests : TestBase
    {
        [Theory]
        [InlineData("chrome", "chrome")]
        [InlineData(" WINWORD.EXE ", "WINWORD")]
        [InlineData("Code - Insiders", "Code - Insiders")]
        [InlineData("notepad++", "notepad++")]
        [InlineData("Öğrenciİşleri", "Öğrenciİşleri")]
        public void ProcessNames_AreAccepted(string raw, string expected) => Assert.Equal(expected, ActiveApp.Sanitize(raw));

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData(".exe")]
        [InlineData("Sınav: cevaplar.docx")]
        [InlineData("a\r\nb")]
        [InlineData("x\"}")]
        public void AnythingElse_IsRejected(string raw) => Assert.Null(ActiveApp.Sanitize(raw));

        [Fact]
        public void TooLong_IsRejected() => Assert.Null(ActiveApp.Sanitize(new string('a', ActiveApp.MaxLength + 1)));
    }
}
