using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using POps.Shared;
using Xunit;

namespace POps.Tests.Agent
{
    // Updater'ın MSI geri dönüşü. Sahada (0.1.4 -> 0.1.5, 0.1.5 -> 0.1.6 tatbikatları) geri kurulum 0 dönüp servisi ve
    // exe'yi yok bırakıyordu; ajanı ancak 90 sn sonra son çare /fvamus geri getiriyordu. DESKTOP-JBSQMIH'de elle:
    // updater'ın komutu -> "Disallowing installation of component", servis yok; + REINSTALLMODE=amus -> 0.1.5 10 sn'de açıldı.
    public class UpdaterRollbackTests : TestBase
    {
        private const string Package = @"C:\POpsData\packages\previous.msi";
        private const string Folder = " INSTALLFOLDER=\"C:\\Program Files\\POps\"";

        // Makineyi taklit eder: her msiexec çağrısından sonra servis/exe durumunu AfterMsiexec belirler
        private sealed class Machine
        {
            public readonly List<string> Msiexec = new List<string>();
            public readonly Queue<int> ExitCodes = new Queue<int>();
            public Action<string> AfterMsiexec = _ => { };
            public bool Service = true;
            public string Exe = "0.1.5.0";
            public bool ProductInstalled = true;
            public bool Healthy = true;
            public int HealthWaits, Sleeps, EnsureRunning;

            public UpdaterRollback.Steps Steps() => new UpdaterRollback.Steps
            {
                RunMsiexec = (arguments, log) =>
                {
                    Msiexec.Add(arguments);
                    AfterMsiexec(arguments);
                    return ExitCodes.Count > 0 ? ExitCodes.Dequeue() : 0;
                },
                ServiceExists = () => Service,
                AgentExeVersion = () => Exe,
                PackageProductInstalled = () => ProductInstalled,
                EnsureServiceRunning = () => EnsureRunning++,
                WaitForHealth = () => { HealthWaits++; return Healthy; },
                Sleep = _ => Sleeps++,
                Log = (message, error) => { },
            };

            public (string Outcome, string Rollback, string Detail) Rollback() =>
                UpdaterRollback.RunMsi(Steps(), Package, Folder, "0.1.5-alpha", "0.1.6-alpha");
        }

        // Sahadaki durum: geri kurulum 0 döner, servis ve exe yok; onarım (/fvamus) ikisini geri getirir
        private static Machine FieldCase()
        {
            var m = new Machine { Service = false, Exe = null };
            m.AfterMsiexec = arguments =>
            {
                if (arguments.StartsWith("/fvamus", StringComparison.Ordinal)) { m.Service = true; m.Exe = "0.1.5.0"; }
            };
            return m;
        }

        // ------------------------------------------------------------------ komutlar

        [Fact]
        public void InstallArguments_ForceEveryFileAndKeepTheFolder()
        {
            string args = UpdaterRollback.InstallArguments(Package, Folder);
            Assert.StartsWith($"/i \"{Package}\" /qn /norestart REBOOT=ReallySuppress", args);
            Assert.Contains(" POPS_ROLLBACK=1", args);
            Assert.Contains(" REINSTALLMODE=amus", args);
            Assert.EndsWith(Folder, args);
        }

        [Fact]
        public void RepairArguments_InstalledProduct_IsForcedRepair()
        {
            string args = UpdaterRollback.RepairArguments(Package, true, Folder);
            Assert.Equal($"/fvamus \"{Package}\" /qn /norestart REBOOT=ReallySuppress", args);
        }

        [Fact]
        public void RepairArguments_ProductNotRegistered_IsForcedRollbackInstall() =>
            Assert.Equal(UpdaterRollback.InstallArguments(Package, Folder), UpdaterRollback.RepairArguments(Package, false, Folder));

        // ------------------------------------------------------------------ ajan yerinde mi

        [Theory]
        [InlineData(true, "0.1.5.0", "0.1.5-alpha", null)]
        [InlineData(true, "0.1.5.0", "v0.1.5-alpha", null)]
        [InlineData(false, null, "0.1.5-alpha", "POpsAgent servisi ve POpsAgent.exe yok")]
        [InlineData(false, "0.1.5.0", "0.1.5-alpha", "POpsAgent servisi yok")]
        [InlineData(true, null, "0.1.5-alpha", "POpsAgent.exe yok")]
        [InlineData(true, "0.1.6.0", "0.1.5-alpha", "POpsAgent.exe 0.1.6.0 (beklenen 0.1.5-alpha)")]
        public void MissingAgent(bool service, string exe, string expected, string missing) =>
            Assert.Equal(missing, UpdaterRollback.MissingAgent(service, exe, expected));

        [Theory]
        [InlineData("0.1.5.0", "0.1.5-alpha", true)]
        [InlineData("0.1.5.0", "V0.1.5", true)]
        [InlineData("0.1.10.0", "0.1.10-beta+7", true)]
        [InlineData("0.1.6.0", "0.1.5-alpha", false)]
        [InlineData("0.1.5.0", "0.1", false)]
        [InlineData(null, "0.1.5-alpha", false)]
        [InlineData("0.1.x.0", "0.1.5-alpha", false)]
        public void SameRelease(string fileVersion, string version, bool same) =>
            Assert.Equal(same, UpdaterRollback.SameRelease(fileVersion, version));

        // ------------------------------------------------------------------ akış ve sonuç

        [Fact]
        public void InstallLeavesAgentInPlace_NoRepair()
        {
            var m = new Machine();
            var (outcome, rollback, detail) = m.Rollback();
            Assert.Equal(new[] { UpdaterRollback.InstallArguments(Package, Folder) }, m.Msiexec);
            Assert.Equal(("rolled_back", "msi"), (outcome, rollback));
            Assert.Equal("0.1.6-alpha sağlıklı açılmadı; 0.1.5-alpha geri kuruldu", detail);
        }

        [Fact]
        public void ServiceAndExeMissing_RepairedAtOnce_StillRolledBack()
        {
            Machine m = FieldCase();
            var (outcome, rollback, detail) = m.Rollback();
            Assert.Equal(2, m.Msiexec.Count);
            Assert.Equal($"/fvamus \"{Package}\" /qn /norestart REBOOT=ReallySuppress", m.Msiexec[1]);
            // Onarımdan önce sağlık için beklenmez (90 sn boşa gitmez); onarımdan sonra bir kez beklenir
            Assert.Equal(1, m.HealthWaits);
            Assert.Equal(1, m.EnsureRunning);
            Assert.Equal(("rolled_back", "msi_repair"), (outcome, rollback));
            Assert.Contains("POpsAgent servisi ve POpsAgent.exe yok", detail);
            Assert.Contains("aynı paketle onarıldı (0)", detail);
            Assert.EndsWith("0.1.5-alpha sağlıklı açıldı", detail);
        }

        [Fact]
        public void WrongExeVersion_IsRepaired()
        {
            var m = new Machine { Exe = "0.1.6.0" };
            m.AfterMsiexec = arguments => { if (arguments.StartsWith("/fvamus", StringComparison.Ordinal)) m.Exe = "0.1.5.0"; };
            var (outcome, rollback, detail) = m.Rollback();
            Assert.Equal(("rolled_back", "msi_repair"), (outcome, rollback));
            Assert.Contains("POpsAgent.exe 0.1.6.0 (beklenen 0.1.5-alpha)", detail);
        }

        [Fact]
        public void ProductNotRegistered_RepairIsAForcedReinstall()
        {
            Machine m = FieldCase();
            m.ProductInstalled = false;
            m.AfterMsiexec = arguments => { if (m.Msiexec.Count == 2) { m.Service = true; m.Exe = "0.1.5.0"; } };
            var (outcome, rollback, _) = m.Rollback();
            Assert.Equal(new[] { UpdaterRollback.InstallArguments(Package, Folder), UpdaterRollback.InstallArguments(Package, Folder) }, m.Msiexec);
            Assert.Equal(("rolled_back", "msi_repair"), (outcome, rollback));
        }

        [Fact]
        public void RepairDoesNotBringTheAgentBack_FailsWithoutWaitingForHealth()
        {
            var m = new Machine { Service = false, Exe = null };
            m.ExitCodes.Enqueue(0);
            m.ExitCodes.Enqueue(1603);
            var (outcome, rollback, detail) = m.Rollback();
            Assert.Equal(0, m.HealthWaits);
            Assert.Equal(("rollback_failed", "msi_repair"), (outcome, rollback));
            Assert.Contains("onarım (1603) sonrasında da POpsAgent servisi ve POpsAgent.exe yok", detail);
        }

        [Fact]
        public void RepairedButUnhealthy_Fails()
        {
            Machine m = FieldCase();
            m.Healthy = false;
            var (outcome, rollback, detail) = m.Rollback();
            Assert.Equal(("rollback_failed", "msi_repair"), (outcome, rollback));
            Assert.EndsWith("ama 0.1.5-alpha sağlıklı açılmadı", detail);
        }

        [Fact]
        public void InPlaceButUnhealthy_Fails()
        {
            var m = new Machine { Healthy = false };
            var (outcome, rollback, _) = m.Rollback();
            Assert.Single(m.Msiexec);
            Assert.Equal(("rollback_failed", "msi"), (outcome, rollback));
        }

        [Fact]
        public void InstallFails_RetriedOnce_NoRepair()
        {
            var m = new Machine();
            m.ExitCodes.Enqueue(1603);
            m.ExitCodes.Enqueue(1603);
            var (outcome, rollback, detail) = m.Rollback();
            Assert.Equal(2, m.Msiexec.Count);
            Assert.All(m.Msiexec, a => Assert.Contains("REINSTALLMODE=amus", a));
            Assert.Equal(1, m.Sleeps);
            Assert.Equal(0, m.HealthWaits);
            Assert.Equal(("rollback_failed", "msi"), (outcome, rollback));
            Assert.Contains("geri kurulum 1603 döndü", detail);
        }

        [Fact]
        public void PendingReboot_IsNotRepaired()
        {
            Machine m = FieldCase();
            m.ExitCodes.Enqueue(3010);
            var (outcome, _, _) = m.Rollback();
            Assert.Single(m.Msiexec);
            Assert.Equal("rollback_pending_reboot", outcome);
        }

        // ------------------------------------------------------------------ paket

        // Elle yapılan geri kurulum (msiexec /i ... POPS_ROLLBACK=1) da bütün dosyaları yazar
        [Fact]
        public void Package_ForcesEveryFileOnRollback()
        {
            string wxs = File.ReadAllText(Path.Combine(TestEnvironment.RepoRoot(), "Installer", "agent", "Package.wxs"));
            string setProperty = wxs.Split('<').FirstOrDefault(e => e.StartsWith("SetProperty Id=\"REINSTALLMODE\"", StringComparison.Ordinal));
            Assert.NotNull(setProperty);
            Assert.Contains("Value=\"amus\"", setProperty);
            Assert.Contains("Before=\"CostInitialize\"", setProperty);
            Assert.Contains("Sequence=\"both\"", setProperty);
            Assert.Contains("Condition=\"POPS_ROLLBACK = &quot;1&quot; AND NOT REINSTALLMODE\"", setProperty);
        }
    }
}
