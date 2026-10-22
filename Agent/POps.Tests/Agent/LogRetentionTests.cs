using System;
using System.IO;
using System.Linq;
using POps.Shared;
using Xunit;

namespace POps.Tests.Agent
{
    // C:\POpsLogs saklama: 30 gün + 200 MB; bugünün logu ve başka dosyalar kalır
    public class LogRetentionTests : TestBase
    {
        private static readonly DateTime Now = new DateTime(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);

        private static LogRetention.LogFile File(string name, int daysOld, long size = 1) =>
            new LogRetention.LogFile(name, size, Now.AddDays(-daysOld));

        [Fact]
        public void Select_DeletesOlderThan30Days_ButNeverTheCurrentLog()
        {
            var files = new[] { File("POps_20260801.log", 65), File("POps_20260904.log", 31), File("POps_20260906.log", 29), File("POps_20261005.log", 40) };
            var delete = LogRetention.Select(files, Now, "POps_20261005.log", LogRetention.MaxAge, LogRetention.MaxTotalBytes);
            Assert.Equal(new[] { "POps_20260801.log", "POps_20260904.log" }, delete);
        }

        [Fact]
        public void Select_SizeCap_DeletesOldestFirst_UntilUnderTheCap()
        {
            const long mb = 1024 * 1024;
            var files = new[]
            {
                File("POps_20261001.log", 4, 90 * mb), File("POps_20261002.log", 3, 90 * mb),
                File("msi-install-0.1.22.log", 2, 30 * mb), File("POps_20261005.log", 0, 50 * mb),
            };
            // Toplam 260 MB; bugünün 50 MB'ı sayılır ama silinmez: en eski 90 MB'lık silinince 170 MB
            var delete = LogRetention.Select(files, Now, "POps_20261005.log", LogRetention.MaxAge, LogRetention.MaxTotalBytes);
            Assert.Equal(new[] { "POps_20261001.log" }, delete);

            delete = LogRetention.Select(files, Now, "POps_20261005.log", LogRetention.MaxAge, 60 * mb);
            Assert.Equal(new[] { "POps_20261001.log", "POps_20261002.log", "msi-install-0.1.22.log" }, delete);
        }

        [Fact]
        public void Apply_TouchesOnlyPopsLogsInTheFolder()
        {
            string dir = TestEnvironment.NewDir("logs-retention");
            string Write(string name, int daysOld)
            {
                string path = Path.Combine(dir, name);
                System.IO.File.WriteAllText(path, "x");
                System.IO.File.SetLastWriteTimeUtc(path, Now.AddDays(-daysOld));
                return path;
            }
            Write("POps_20260801.log", 65);
            Write("msi-install-0.1.10.log", 60);
            string current = Write("POps_20261005.log", 0);
            string recent = Write("POps_20260930.log", 5);
            string other = Write("notlar.txt", 90);
            string userLog = Write("POpsWatchdog_20260801.log", 65);
            Directory.CreateDirectory(Path.Combine(dir, "alt"));
            string nested = Path.Combine(dir, "alt", "POps_20260101.log");
            System.IO.File.WriteAllText(nested, "x");
            System.IO.File.SetLastWriteTimeUtc(nested, Now.AddDays(-200));

            var (deleted, freed) = LogRetention.Apply(dir, LogRetention.MachinePatterns, Now, current);
            Assert.Equal(2, deleted);
            Assert.Equal(2, freed);
            Assert.Equal(new[] { current, recent, other, userLog }.OrderBy(p => p), Directory.GetFiles(dir).OrderBy(p => p));
            Assert.True(System.IO.File.Exists(nested));
        }

        [Fact]
        public void Apply_MissingFolder_IsNothing() =>
            Assert.Equal((0, 0L), LogRetention.Apply(Path.Combine(TestEnvironment.Root, "yok"), LogRetention.MachinePatterns, Now, null));
    }
}
