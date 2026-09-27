using System;
using System.Collections.Generic;
using System.Linq;
using POpsAgent;
using Xunit;

namespace POps.Tests.Agent
{
    // Kayıt defteri okunmaz: Uninstall alt anahtarlarının değerleri sahte sözlüklerle verilir
    public class SoftwareInventoryTests : TestBase
    {
        private static IReadOnlyDictionary<string, object> Entry(params (string Name, object Value)[] values) =>
            values.ToDictionary(v => v.Name, v => v.Value, StringComparer.OrdinalIgnoreCase);

        [Fact]
        public void RegularEntry_KeepsFieldsAsTheyAre()
        {
            SoftwareItem item = SoftwareInventory.FromEntry(Entry(("DisplayName", " 7-Zip 23.01 (x64) "), ("DisplayVersion", "23.01"), ("Publisher", "Igor Pavlov"), ("InstallDate", "20240115")));
            Assert.Equal("7-Zip 23.01 (x64)", item.Name);
            Assert.Equal("23.01", item.Version);
            Assert.Equal("Igor Pavlov", item.Publisher);
            Assert.Equal("20240115", item.InstallDate);
        }

        [Fact]
        public void MissingOptionalFields_VersionEmptyOthersNull()
        {
            SoftwareItem item = SoftwareInventory.FromEntry(Entry(("DisplayName", "Tool")));
            Assert.Equal("", item.Version);
            Assert.Null(item.Publisher);
            Assert.Null(item.InstallDate);
        }

        [Theory]
        [InlineData(1)]
        [InlineData("1")]
        public void SystemComponent_IsSkipped(object flag) =>
            Assert.Null(SoftwareInventory.FromEntry(Entry(("DisplayName", "Windows bileşeni"), ("SystemComponent", flag))));

        [Fact]
        public void SystemComponentZero_IsKept() =>
            Assert.NotNull(SoftwareInventory.FromEntry(Entry(("DisplayName", "Uygulama"), ("SystemComponent", 0))));

        [Fact]
        public void ParentKeyName_IsSkipped() =>
            Assert.Null(SoftwareInventory.FromEntry(Entry(("DisplayName", "Office dil paketi"), ("ParentKeyName", "Office16.PROPLUS"))));

        [Theory]
        [InlineData("Update")]
        [InlineData("Hotfix")]
        [InlineData("Security Update")]
        [InlineData("security update")]
        public void UpdateReleaseTypes_AreSkipped(string releaseType) =>
            Assert.Null(SoftwareInventory.FromEntry(Entry(("DisplayName", "KB123456"), ("ReleaseType", releaseType))));

        [Fact]
        public void OtherReleaseType_IsKept() =>
            Assert.NotNull(SoftwareInventory.FromEntry(Entry(("DisplayName", "Office"), ("ReleaseType", "Service Pack"))));

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void EmptyDisplayName_IsSkipped(string name) =>
            Assert.Null(SoftwareInventory.FromEntry(Entry(("DisplayName", name), ("DisplayVersion", "1.0"))));

        [Fact]
        public void Build_FiltersDeduplicatesAndSorts()
        {
            var entries = new[]
            {
                Entry(("DisplayName", "zoom"), ("DisplayVersion", "5.0")),
                Entry(("DisplayName", "Chrome"), ("DisplayVersion", "120"), ("Publisher", "Google")),
                // Aynı ürün 64 bit ve WOW6432Node altında: tek kayıt, eksik alan diğerinden tamamlanır
                Entry(("DisplayName", "Chrome"), ("DisplayVersion", "120"), ("InstallDate", "20240101")),
                Entry(("DisplayName", "Chrome"), ("DisplayVersion", "121")),
                Entry(("DisplayName", "Güncelleme"), ("ReleaseType", "Update")),
                Entry(("DisplayName", "Bileşen"), ("SystemComponent", 1)),
                Entry(("DisplayVersion", "adsız")),
            };

            List<SoftwareItem> items = SoftwareInventory.Build(entries);

            Assert.Equal(new[] { "Chrome 120", "Chrome 121", "zoom 5.0" }, items.Select(i => $"{i.Name} {i.Version}"));
            Assert.Equal("Google", items[0].Publisher);
            Assert.Equal("20240101", items[0].InstallDate);
        }

        [Fact]
        public void Build_IsCappedAt5000()
        {
            var entries = Enumerable.Range(0, SoftwareInventory.MaxItems + 25).Select(i => Entry(("DisplayName", $"App {i:D5}")));
            List<SoftwareItem> items = SoftwareInventory.Build(entries);
            Assert.Equal(SoftwareInventory.MaxItems, items.Count);
            Assert.Equal("App 00000", items[0].Name);
        }

        [Fact]
        public void Hash_DependsOnContentNotOnRegistryOrder()
        {
            var a = new[] { Entry(("DisplayName", "A"), ("DisplayVersion", "1")), Entry(("DisplayName", "B"), ("DisplayVersion", "2")) };
            var b = Enumerable.Reverse(a).ToArray();
            var changed = new[] { Entry(("DisplayName", "A"), ("DisplayVersion", "1")), Entry(("DisplayName", "B"), ("DisplayVersion", "3")) };

            Assert.Equal(SoftwareInventory.Hash(SoftwareInventory.Build(a)), SoftwareInventory.Hash(SoftwareInventory.Build(b)));
            Assert.NotEqual(SoftwareInventory.Hash(SoftwareInventory.Build(a)), SoftwareInventory.Hash(SoftwareInventory.Build(changed)));
        }

        [Theory]
        [InlineData("S-1-5-21-1004336348-1177238915-682003330-1001", true)]
        [InlineData("S-1-5-21-1004336348-1177238915-682003330-1001_Classes", false)]
        [InlineData("S-1-5-18", false)]
        [InlineData(".DEFAULT", false)]
        public void OnlyRealUserHivesAreRead(string name, bool expected) => Assert.Equal(expected, SoftwareInventory.IsUserHive(name));
    }

    // "Liste değişmediyse gönderme; yine de günde en az bir kez gönder"
    public class ReportGateTests : TestBase
    {
        private static readonly DateTime T0 = new DateTime(2026, 9, 27, 8, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void FirstReport_IsAlwaysSent() => Assert.True(new ReportGate(TimeSpan.FromDays(1)).ShouldSend("h1", T0));

        [Fact]
        public void UnchangedList_IsNotSentAgainWithinADay()
        {
            var gate = new ReportGate(TimeSpan.FromDays(1));
            gate.MarkSent("h1", T0);
            Assert.False(gate.ShouldSend("h1", T0.AddHours(6)));
            Assert.False(gate.ShouldSend("h1", T0.AddHours(23.9)));
        }

        [Fact]
        public void UnchangedList_IsSentOnceADayAnyway()
        {
            var gate = new ReportGate(TimeSpan.FromDays(1));
            gate.MarkSent("h1", T0);
            Assert.True(gate.ShouldSend("h1", T0.AddHours(24)));
        }

        [Fact]
        public void ChangedList_IsSentImmediately()
        {
            var gate = new ReportGate(TimeSpan.FromDays(1));
            gate.MarkSent("h1", T0);
            Assert.True(gate.ShouldSend("h2", T0.AddMinutes(1)));
        }

        [Fact]
        public void FailedSend_IsRetried()
        {
            var gate = new ReportGate(TimeSpan.FromDays(1));
            gate.MarkSent("h1", T0);
            // h2 gönderilemedi (MarkSent çağrılmadı): sonraki turda yine gönderilmeli
            Assert.True(gate.ShouldSend("h2", T0.AddHours(6)));
            Assert.True(gate.ShouldSend("h2", T0.AddHours(12)));
        }
    }
}
