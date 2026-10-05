using System;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using POps.Shared;
using POpsAgent;
using Xunit;

namespace POps.Tests.Agent
{
    // appsettings.json LogDirectory / DataDirectory: değer kuralları, varsayılana dönüş ve bildirim, klasör izinleri,
    // servis / updater / watchdog'un aynı klasörleri seçmesi. Klasörler hep geçici test klasöründe.
    public class FolderSettingsTests : TestBase, IDisposable
    {
        private readonly string _root = TestEnvironment.NewDir("folders");
        private readonly string _defaultData;
        private readonly string _defaultLog;

        // Makinedeki kurallar (yalnızca değer denetimi; diske dokunmaz)
        private static readonly FolderRules Machine = FolderRules.ForMachine(@"C:\Program Files\POps");

        private static readonly SecurityIdentifier Me = WindowsIdentity.GetCurrent().User;
        private static readonly SecurityIdentifier Admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        private static readonly SecurityIdentifier Users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        private static readonly SecurityIdentifier AuthenticatedUsers = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);

        public FolderSettingsTests()
        {
            _defaultData = Path.Combine(_root, "default-data");
            _defaultLog = Path.Combine(_root, "default-logs");
        }

        public void Dispose()
        {
            POpsHelpers.ConfigPaths = new string[0];
            AgentDirectories.Problem = null;
            AgentUpdate.DataDir = TestEnvironment.DefaultDataDir;
            SecureStore.Dir = TestEnvironment.DefaultSecureDir;
            ServerTrust.CaPath = Path.Combine(TestEnvironment.DefaultSecureDir, ServerTrust.FileName);
            POpsHelpers.MachineLogDir = TestEnvironment.DefaultMachineLogDir;
        }

        // Üst klasör denetimi test klasöründe durur (kullanıcı profilinin izinleri makineden makineye değişir)
        private FolderRules Rules() => new FolderRules { TrustedBase = _root };

        private string Dir(params string[] parts) => Path.Combine(new[] { _root }.Concat(parts).ToArray());

        private void Config(object settings)
        {
            string path = Path.Combine(TestEnvironment.NewDir("folders-config"), "appsettings.json");
            File.WriteAllText(path, JsonSerializer.Serialize(settings));
            POpsHelpers.ConfigPaths = new[] { path };
        }

        private static FileSystemAccessRule[] AclRules(string dir) =>
            new DirectoryInfo(dir).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();

        private static void Grant(string dir, SecurityIdentifier sid, FileSystemRights rights)
        {
            var info = new DirectoryInfo(dir);
            DirectorySecurity sec = info.GetAccessControl();
            sec.AddAccessRule(new FileSystemAccessRule(sid, rights, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            info.SetAccessControl(sec);
        }

        // ---------------------------------------------------------------- değer

        [Theory]
        [InlineData(@"D:\POpsData", @"D:\POpsData")]
        [InlineData(@"  d:/Kurum/POps Data/  ", @"d:\Kurum\POps Data")]
        [InlineData(@"E:\POpsLogs\", @"E:\POpsLogs")]
        [InlineData(@"C:\Program Files\POpsData", @"C:\Program Files\POpsData")]
        [InlineData(@"C:\ProgramData\POps", @"C:\ProgramData\POps")]
        public void ValidValue_IsNormalized(string value, string expected)
        {
            Assert.Null(FolderSettings.CheckValue(value, Machine, out string full));
            Assert.Equal(expected, full);
        }

        [Theory]
        [InlineData("POpsData", "sürücü harfiyle")]
        [InlineData(@"..\POpsData", "sürücü harfiyle")]
        [InlineData("C:POpsData", "sürücü harfiyle")]
        [InlineData(@"\POpsData", "sürücü harfiyle")]
        [InlineData("D:", "sürücü harfiyle")]
        [InlineData(@"\\server\share\POps", "ağ yolu")]
        [InlineData("//server/share/POps", "ağ yolu")]
        [InlineData(@"\\?\C:\POpsData", "aygıt yolu")]
        [InlineData(@"\\.\C:\POpsData", "aygıt yolu")]
        [InlineData(@"\\?\UNC\server\share\POps", "aygıt yolu")]
        [InlineData(@"\??\C:\POpsData", "aygıt yolu")]
        [InlineData(@"D:\POps*", "joker")]
        [InlineData(@"D:\POps?\Data", "joker")]
        [InlineData("D:\\POps\tData", "joker")]
        [InlineData(@"D:\POpsData:gizli", "':'")]
        [InlineData(@"D:\POps:x\Data", "':'")]
        [InlineData(@"D:\POps\..\Windows", "'..'")]
        [InlineData(@"D:\.\POpsData", "'..'")]
        [InlineData(@"D:\POps\NUL", "ayrılmış")]
        [InlineData(@"D:\com1.data", "ayrılmış")]
        [InlineData(@"D:\POpsData.", "nokta ya da boşluk")]
        [InlineData(@"D:\", "sürücü kökü")]
        [InlineData(@"C:\Program Files", "içinde ayrı bir klasör")]
        [InlineData(@"C:\ProgramData", "içinde ayrı bir klasör")]
        [InlineData(@"C:\Program Files\POps", "içindeki bir klasör olamaz")]
        [InlineData(@"C:\Program Files\POps\Data", "içindeki bir klasör olamaz")]
        [InlineData(@"C:\Users", "içindeki bir klasör olamaz")]
        [InlineData(@"C:\Users\Public\POps", "içindeki bir klasör olamaz")]
        public void InvalidValue_IsRefused(string value, string expected)
        {
            string error = FolderSettings.CheckValue(value, Machine, out string full);
            Assert.NotNull(error);
            Assert.Contains(expected, error);
            Assert.Equal("", full);
        }

        [Fact]
        public void WindowsFolder_ItsSubfoldersAndTooLongPaths_AreRefused()
        {
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            Assert.Contains("içindeki bir klasör olamaz", FolderSettings.CheckValue(windows, Machine, out _));
            Assert.Contains("içindeki bir klasör olamaz", FolderSettings.CheckValue(Path.Combine(windows, "Temp", "POps"), Machine, out _));
            Assert.Contains("karakter", FolderSettings.CheckValue(@"D:\" + new string('a', FolderSettings.MaxLength), Machine, out _));
        }

        // Korunan bir klasörün üst klasörü de olamaz: izinleri onun içine de geçerdi
        [Fact]
        public void ParentOfAProtectedFolder_IsRefused()
        {
            var rules = new FolderRules();
            rules.Trees.Add(@"D:\Apps\POps");
            rules.Roots.Add(@"E:\Kurum\Program Files");
            Assert.Contains(@"D:\Apps\POps klasörünün üst klasörü olamaz", FolderSettings.CheckValue(@"D:\Apps", rules, out _));
            Assert.Contains(@"E:\Kurum\Program Files klasörünün üst klasörü olamaz", FolderSettings.CheckValue(@"E:\Kurum", rules, out _));
            Assert.Null(FolderSettings.CheckValue(@"D:\Apps2", rules, out _));
            Assert.Null(FolderSettings.CheckValue(@"E:\Kurum\Program Files\POpsData", rules, out _));
        }

        // ---------------------------------------------------------------- seçim ve varsayılana dönüş

        [Fact]
        public void Unset_UsesTheDefaults_WithoutAProblem()
        {
            FolderSettings folders = FolderSettings.Resolve(null, "  ", Rules(), true, _defaultData, _defaultLog);
            Assert.Equal(_defaultData, folders.DataDirectory);
            Assert.Equal(_defaultLog, folders.LogDirectory);
            Assert.False(folders.CustomData || folders.CustomLog);
            Assert.Null(folders.Problem);
        }

        [Fact]
        public void TheDefaultWrittenOut_IsStillTheDefault()
        {
            FolderSettings folders = FolderSettings.Resolve(_defaultData.ToUpperInvariant() + "\\", null, Rules(), true, _defaultData, _defaultLog);
            Assert.False(folders.CustomData);
            Assert.Null(folders.Problem);
        }

        [Fact]
        public void InvalidValue_FallsBackToTheDefault_WithAProblem()
        {
            FolderSettings folders = FolderSettings.Resolve(@"\\server\share\POps", Dir("logs"), Rules(), true, _defaultData, _defaultLog);
            Assert.Equal(_defaultData, folders.DataDirectory);
            Assert.Equal(Dir("logs"), folders.LogDirectory);
            Assert.Contains(@"DataDirectory geçersiz (\\server\share\POps)", folders.Problem);
            Assert.Contains("ağ yolu", folders.Problem);
            Assert.Contains($"varsayılan {_defaultData} kullanılıyor", folders.Problem);
        }

        [Theory]
        [InlineData("data", "data")]
        [InlineData("data", @"data\logs")]
        [InlineData(@"logs\data", "logs")]
        public void LogFolderOverlappingTheDataFolder_FallsBack(string data, string log)
        {
            FolderSettings folders = FolderSettings.Resolve(Dir(data), Dir(log), Rules(), true, _defaultData, _defaultLog);
            Assert.Equal(Dir(data), folders.DataDirectory);
            Assert.Equal(_defaultLog, folders.LogDirectory);
            Assert.Contains("LogDirectory", folders.Problem);
            Assert.Contains("veri klasörüyle", folders.Problem);
        }

        // ---------------------------------------------------------------- konum (disk ve üst klasörler)

        [Fact]
        public void Location_SafeParents_AreAccepted()
        {
            Directory.CreateDirectory(Dir("kurum"));
            Assert.Null(FolderSettings.CheckLocation(Dir("kurum", "POpsData"), Rules()));
            Assert.Null(FolderSettings.CheckLocation(Dir("yok", "POpsData"), Rules()));   // eksik üst klasörü Secure korumalı açar
        }

        // Kullanıcıların silip yeniden adlandırabildiği bir üst klasör: içi değiştirilebilirdi (updater SYSTEM olarak çalışır)
        [Fact]
        public void Location_ParentThatUsersCanReplace_IsRefused()
        {
            Directory.CreateDirectory(Dir("open"));
            Grant(Dir("open"), AuthenticatedUsers, FileSystemRights.Modify);
            string error = FolderSettings.CheckLocation(Dir("open", "POpsData"), Rules());
            Assert.Contains("üst klasörü", error);

            FolderSettings folders = FolderSettings.Resolve(Dir("open", "POpsData"), null, Rules(), true, _defaultData, _defaultLog);
            Assert.Equal(_defaultData, folders.DataDirectory);
            Assert.NotNull(folders.Problem);
        }

        [Fact]
        public void Location_MissingDrive_IsRefused()
        {
            char? free = Enumerable.Range('D', 23).Reverse().Select(c => (char?)(char)c)
                .FirstOrDefault(c => !DriveInfo.GetDrives().Any(d => char.ToUpperInvariant(d.Name[0]) == c));
            if (free == null) return;   // bütün harfler dolu
            Assert.Contains("sürücüsü yok", FolderSettings.CheckLocation($@"{free}:\POpsData", Rules()));
        }

        // ---------------------------------------------------------------- izinler

        [Fact]
        public void Secure_CreatesTheFolderAndMissingParents_Protected()
        {
            string data = Dir("a", "b", "POpsData");
            Assert.Null(FolderSettings.Secure(data, usersRead: true, out bool tightened));
            Assert.False(tightened);

            DirectorySecurity sec = new DirectoryInfo(data).GetAccessControl();
            Assert.True(sec.AreAccessRulesProtected);
            FileSystemAccessRule[] rules = AclRules(data);
            Assert.Equal(3, rules.Length);
            Assert.Contains(rules, r => r.IdentityReference.Equals(Me) && r.FileSystemRights == FileSystemRights.FullControl);
            Assert.Contains(rules, r => r.IdentityReference.Equals(Admins) && r.FileSystemRights == FileSystemRights.FullControl);
            Assert.Contains(rules, r => r.IdentityReference.Equals(Users) && (r.FileSystemRights & FileSystemRights.WriteData) == 0
                                        && (r.FileSystemRights & FileSystemRights.ReadData) != 0);
            Assert.All(rules, r => Assert.Equal(InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, r.InheritanceFlags));

            // Oluşturulan üst klasörler de korumalı (kullanıcı onları silip yerine kendi klasörünü koyamaz)
            foreach (string parent in new[] { Dir("a"), Dir("a", "b") })
            {
                Assert.True(new DirectoryInfo(parent).GetAccessControl().AreAccessRulesProtected);
                Assert.DoesNotContain(AclRules(parent), r => !r.IdentityReference.Equals(Me) && !r.IdentityReference.Equals(Admins) && !r.IdentityReference.Equals(Users));
            }

            string logs = Dir("logs");
            Assert.Null(FolderSettings.Secure(logs, usersRead: false, out _));
            Assert.True(new DirectoryInfo(logs).GetAccessControl().AreAccessRulesProtected);
            Assert.All(AclRules(logs), r => Assert.True(r.IdentityReference.Equals(Me) || r.IdentityReference.Equals(Admins)));
        }

        // Klasör önceden vardı ve herkese açıktı: daraltılır, bildirilir; ikinci açılışta artık bildirilmez
        [Fact]
        public void Secure_ExistingOpenFolder_IsTightened()
        {
            string dir = Dir("open-data");
            Directory.CreateDirectory(dir);
            Grant(dir, AuthenticatedUsers, FileSystemRights.Modify);
            Grant(dir, Users, FileSystemRights.Modify);

            Assert.Null(FolderSettings.Secure(dir, usersRead: true, out bool tightened));
            Assert.True(tightened);
            Assert.True(new DirectoryInfo(dir).GetAccessControl().AreAccessRulesProtected);
            Assert.DoesNotContain(AclRules(dir), r => r.IdentityReference.Equals(AuthenticatedUsers));
            Assert.All(AclRules(dir).Where(r => r.IdentityReference.Equals(Users)), r => Assert.Equal(0, (int)(r.FileSystemRights & FileSystemRights.Modify & ~FileSystemRights.ReadAndExecute)));

            Assert.Null(FolderSettings.Secure(dir, usersRead: true, out tightened));
            Assert.False(tightened);
        }

        // Seçilen klasör oluşturulamıyor (ör. aynı adda bir dosya var): varsayılana dönülür, sorun bildirilir
        [Fact]
        public void SecureCustom_FolderThatCannotBeCreated_FallsBack()
        {
            File.WriteAllText(Dir("blocker"), "x");
            FolderSettings folders = FolderSettings.Resolve(Dir("blocker", "POpsData"), Dir("logs"), Rules(), true, _defaultData, _defaultLog);
            Assert.True(folders.CustomData);
            folders.SecureCustom();
            Assert.Equal(_defaultData, folders.DataDirectory);
            Assert.Equal(Dir("logs"), folders.LogDirectory);
            Assert.Contains("DataDirectory klasörü", folders.Problem);
            Assert.True(Directory.Exists(Dir("logs")));
        }

        // ---------------------------------------------------------------- servis, updater, watchdog

        [Fact]
        public void Service_UsesTheConfiguredFolders_AndTheUpdaterAndWatchdogGetTheSame()
        {
            string data = Dir("D", "POpsData"), logs = Dir("E", "POpsLogs");
            Config(new { ServerUrl = "https://pops.example", DataDirectory = data, LogDirectory = logs });

            FolderSettings service = AgentDirectories.Apply(secure: true, Rules(), _defaultData, _defaultLog);
            Assert.Null(AgentDirectories.Problem);
            Assert.Equal(data, AgentUpdate.DataDir);
            Assert.Equal(Path.Combine(data, "secure"), SecureStore.Dir);
            Assert.Equal(Path.Combine(data, "secure", ServerTrust.FileName), ServerTrust.CaPath);
            Assert.Equal(Path.Combine(data, "identity.key"), AgentUpdate.IdentityPath);
            Assert.Equal(logs, POpsHelpers.MachineLogDir);
            Assert.True(new DirectoryInfo(data).GetAccessControl().AreAccessRulesProtected);
            Assert.True(new DirectoryInfo(logs).GetAccessControl().AreAccessRulesProtected);
            Assert.False(Directory.Exists(_defaultData));

            // Updater: servisin komut satırından, aynı kurallarla
            FolderSettings updater = FolderSettings.FromArguments(AgentDirectories.UpdaterArguments().ToList(), Rules(), checkLocation: true);
            Assert.Equal((service.DataDirectory, service.LogDirectory), (updater.DataDirectory, updater.LogDirectory));
            Assert.Null(updater.Problem);

            // Watchdog: --datadir "<klasör>" (CreateProcessAsUser komut satırı)
            Assert.Equal($"--datadir \"{data}\"", AgentDirectories.WatchdogArguments());
            FolderSettings watchdog = FolderSettings.FromArguments(new[] { "--datadir", data }, Rules(), checkLocation: false);
            Assert.Equal(data, watchdog.DataDirectory);
        }

        // Updater ya da watchdog klasör almadan başlatılırsa varsayılanlar
        [Fact]
        public void NoArguments_MeanTheDefaults()
        {
            FolderSettings folders = FolderSettings.FromArguments(new[] { "--msi", "x.msi", "POpsV" }, Rules(), checkLocation: true);
            Assert.Equal(FolderSettings.DefaultDataDirectory, folders.DataDirectory);
            Assert.Equal(FolderSettings.DefaultLogDirectory, folders.LogDirectory);
            Assert.Null(folders.Problem);
        }

        // Geçersiz ayar: ajan varsayılanla açılır, sorun log ve Olay Günlüğüne (1090) gider; tepside "yönetilemiyor" denmez
        [Fact]
        public void InvalidSetting_IsReportedAsAConfigProblem_ButNotInTheTray()
        {
            Config(new { ServerUrl = "https://pops.example", DataDirectory = @"\\server\share\POps", LogDirectory = "logs" });
            AgentDirectories.Apply(secure: true, Rules(), _defaultData, _defaultLog);
            Assert.Equal(_defaultData, AgentUpdate.DataDir);
            Assert.Equal(_defaultLog, POpsHelpers.MachineLogDir);
            Assert.Contains("DataDirectory geçersiz", AgentDirectories.Problem);
            Assert.Contains("LogDirectory geçersiz (logs)", AgentDirectories.Problem);

            using var worker = new Worker(NullLogger<Worker>.Instance);
            Assert.Null(worker.ConfigProblem);
            Assert.Equal(AgentDirectories.Problem, worker.FolderProblem);
            Assert.Null(worker.ConfigErrorMessage());
            worker.ReportConfigProblem();   // Olay Günlüğü yazılamasa da durmaz

            LocalAuditEvent audit = LocalAudit.ConfigUnreadable(worker.FolderProblem, "https://pops.example");
            Assert.Equal(1090, audit.EventId);
            Assert.Equal(LocalAuditLevel.Error, audit.Level);
            Assert.Contains("DataDirectory geçersiz", audit.Message);
        }

        [Fact]
        public void NonTextSetting_IsAProblem()
        {
            Config(new { ServerUrl = "https://pops.example", DataDirectory = 5 });
            AgentDirectories.Apply(secure: false, Rules(), _defaultData, _defaultLog);
            Assert.Equal(_defaultData, AgentUpdate.DataDir);
            Assert.Contains("DataDirectory bir metin", AgentDirectories.Problem);
        }
    }
}
