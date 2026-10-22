using System;
using System.Collections.Generic;
using System.Linq;
using POpsAgent;
using Xunit;

namespace POps.Tests.Agent
{
    // winget paketinin kurulduğu yer (docs/agent.md "winget_install contract" 7, bkz. WingetLocation): kurulumdan önceki ve
    // sonraki kayıt defterinin sahte görüntülerinden. Bu bilgisayarın kayıt defteri okunmaz, winget çalışmaz. Örnekler saha
    // testinin (2026-10-22, ajan LocalSystem, 0.1.24-alpha) kayıtlarıdır.
    public class WingetLocationTests : TestBase
    {
        internal const string AnkiCode = "{7AD362E6-020E-4F25-AD49-94A408DC5CA5}";
        internal const string AnkiPacked = "6E263DA7E02052F4DA94494A80CDC55A";
        // 7-Zip'in x64 MSI'sının ürün kodu biçiminde (makine geneli MSI)
        internal const string SevenZipCode = "{23170F69-40C1-2702-2408-000001000000}";
        internal const string SevenZipPacked = "96F071321C0420724280000010000000";
        internal const string TelegramKey = "Telegram.TelegramDesktop_Microsoft.Winget.Source_8wekyb3d8bbwe";

        private const string Machine = WingetInstall.MachineWideNote;
        private const string SystemProfile = WingetInstall.SystemProfileNote;

        private static string Unknown(string reason) => WingetInstall.UnknownLocationNote(reason);

        // Kurulumdan önce de sonra da duran kayıtlar: kendini güncelleyen bir tarayıcı ve makine geneli bir çalışma zamanı
        private static UninstallEntry Edge(string version = "130.0.2849.80") => new UninstallEntry(UninstallHive.Machine32, "Microsoft Edge")
        {
            DisplayName = "Microsoft Edge",
            DisplayVersion = version,
            InstallLocation = @"C:\Program Files (x86)\Microsoft\Edge\Application",
            UninstallString = "\"C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\130.0.2849.80\\Installer\\setup.exe\" --uninstall --msedge --system-level",
        };

        private const string RuntimeCode = "{8BDFE669-9705-4184-9368-DB9CE581E0E7}";

        private static UninstallEntry Runtime() => new UninstallEntry(UninstallHive.Machine, RuntimeCode)
        {
            DisplayName = "Microsoft Visual C++ 2022 X64 Minimum Runtime",
            DisplayVersion = "14.40.33810",
            WindowsInstaller = true,
            UninstallString = "MsiExec.exe /I" + RuntimeCode,
        };

        internal static InstallRegistrySnapshot Registry(IEnumerable<UninstallEntry> entries, IEnumerable<string> machineProducts = null,
            IEnumerable<string> systemProducts = null) =>
            new InstallRegistrySnapshot(entries, machineProducts ?? Array.Empty<string>(), systemProducts ?? Array.Empty<string>());

        // Kurulumdan önce: yalnızca arka plandaki kayıtlar
        internal static InstallRegistrySnapshot Before() =>
            Registry(new[] { Edge(), Runtime() }, new[] { WingetLocation.PackedProductCode(RuntimeCode) });

        // Anki 26.09.3: manifestinde "Scope: user" yazan WiX MSI'ı, --scope olmadan yeniden denenerek SYSTEM olarak kuruldu. Dosyalar
        // C:\Windows\System32\config\systemprofile\AppData\Local\Programs\Anki'de, Başlat kısayolu yalnızca SYSTEM'in profilinde;
        // Windows Installer ürünü SYSTEM'e kullanıcı başına kaydetti (HKU\S-1-5-18\Software\Microsoft\Installer\Products), makine
        // geneli kaydı (HKLM\SOFTWARE\Classes\Installer\Products) yok. Kaldırma kaydı ise HKLM'de (64 bit): winget kapsamı onun
        // kovanından çıkarır, "winget list --scope machine" paketi buldu ve 0.1.24-alpha "makine geneli" yazdı.
        internal static UninstallEntry Anki26(string displayIcon = null) => new UninstallEntry(UninstallHive.Machine, AnkiCode)
        {
            DisplayName = "Anki",
            DisplayVersion = "26.09.3",
            WindowsInstaller = true,
            UninstallString = "MsiExec.exe /X" + AnkiCode,
            DisplayIcon = displayIcon,
        };

        internal static InstallRegistrySnapshot Anki26After(string displayIcon = null) =>
            Registry(new[] { Edge(), Runtime(), Anki26(displayIcon) }, new[] { WingetLocation.PackedProductCode(RuntimeCode) }, new[] { AnkiPacked });

        // Anki 2.1.65: manifestinde "Scope: machine" yazan 32 bit NSIS yükleyicisi SYSTEM olarak C:\Windows\SysWOW64\config\
        // systemprofile\AppData\Local\Programs\Anki'ye kurdu; kaldırma kaydı HKU\S-1-5-18'de, UninstallString'i WOW64 yönlendirmesi
        // yüzünden C:\WINDOWS\system32\config\systemprofile\... der.
        internal static UninstallEntry Anki2165(string version = "2.1.65") => new UninstallEntry(UninstallHive.SystemUser, "Anki")
        {
            DisplayName = "Anki",
            DisplayVersion = version,
            UninstallString = @"C:\WINDOWS\system32\config\systemprofile\AppData\Local\Programs\Anki\uninstall.exe",
            DisplayIcon = @"C:\WINDOWS\system32\config\systemprofile\AppData\Local\Programs\Anki\anki.exe",
        };

        internal static InstallRegistrySnapshot Anki2165After() =>
            Registry(new[] { Edge(), Runtime(), Anki2165() }, new[] { WingetLocation.PackedProductCode(RuntimeCode) });

        // 7zip.7zip: makine geneli MSI. Kaldırma kaydı HKLM'de, ürün HKLM\SOFTWARE\Classes\Installer\Products'ta
        internal static InstallRegistrySnapshot SevenZipAfter() =>
            Registry(new[]
            {
                Edge(), Runtime(),
                new UninstallEntry(UninstallHive.Machine, SevenZipCode)
                {
                    DisplayName = "7-Zip 24.08 (x64 edition)", DisplayVersion = "24.08.00.0", WindowsInstaller = true,
                    InstallLocation = @"C:\Program Files\7-Zip\", UninstallString = "MsiExec.exe /I" + SevenZipCode,
                },
            }, new[] { WingetLocation.PackedProductCode(RuntimeCode), SevenZipPacked });

        // Telegram.TelegramDesktop: makine geneli kurulan taşınabilir (zip) paket. winget'in kendi kaldırma kaydı HKLM'de, dosyalar
        // C:\Program Files\WinGet\Packages altında
        internal static InstallRegistrySnapshot TelegramAfter() =>
            Registry(new[]
            {
                Edge(), Runtime(),
                new UninstallEntry(UninstallHive.Machine, TelegramKey)
                {
                    DisplayName = "Telegram Desktop", DisplayVersion = "5.6.3",
                    InstallLocation = @"C:\Program Files\WinGet\Packages\" + TelegramKey,
                    UninstallString = "winget uninstall --product-code " + TelegramKey,
                },
            }, new[] { WingetLocation.PackedProductCode(RuntimeCode) });

        private static LocationDecision Decide(InstallRegistrySnapshot after, int exitCode = 0) => WingetLocation.Decide(exitCode, Before(), after);

        // Windows Installer'ın dönüşümü: ilk üç grup ters, son iki grubun her karakter çifti yer değiştirir (sahadaki anahtar:
        // HKU\S-1-5-18\Software\Microsoft\Installer\Products\6E263DA7E02052F4DA94494A80CDC55A)
        [Theory]
        [InlineData(AnkiCode, AnkiPacked)]
        [InlineData("{7ad362e6-020e-4f25-ad49-94a408dc5ca5}", AnkiPacked)]
        [InlineData(SevenZipCode, SevenZipPacked)]
        [InlineData("{00000000-0000-0000-0000-000000000000}", "00000000000000000000000000000000")]
        [InlineData("{12345678-9ABC-DEF0-1234-56789ABCDEF0}", "87654321CBA90FED21436587A9CBED0F")]
        public void PackedProductCode_IsWindowsInstallersTransform(string productCode, string packed) =>
            Assert.Equal(packed, WingetLocation.PackedProductCode(productCode));

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("Anki")]
        [InlineData(TelegramKey)]
        [InlineData("7AD362E6-020E-4F25-AD49-94A408DC5CA5")]
        [InlineData("{7AD362E6-020E-4F25-AD49-94A408DC5CA}")]
        [InlineData("{7AD362E6020E4F25AD4994A408DC5CA5}")]
        [InlineData("{GAD362E6-020E-4F25-AD49-94A408DC5CA5}")]
        [InlineData("{7AD362E6-020E-4F25-AD49-94A408DC5CA5}.KB5034441")]
        [InlineData("{7AD362E6-020E-4F25-AD49-94A408DC5CA5}\n")]
        public void PackedProductCode_OnlyForAProductCode(string key) => Assert.Null(WingetLocation.PackedProductCode(key));

        // Saha: kaldırma kaydı HKLM'de ama ürün SYSTEM'e kullanıcı başına kayıtlı: SYSTEM'in profili. winget'in kapsamı (kaydın
        // kovanı) burada makine geneli derdi. Dosya yolu da (DisplayIcon) aynı yeri gösterir; yol olmasa da ürün kaydı yeter.
        [Fact]
        public void Anki26_PerUserMsiForSystem_IsSystemsProfile_ThoughTheUninstallEntryIsInHklm()
        {
            LocationDecision decision = Decide(Anki26After());
            Assert.Equal(SystemProfile, decision.Note);
            UninstallEntry entry = Assert.Single(decision.Entries);
            Assert.Equal((UninstallHive.Machine, AnkiCode), (entry.Hive, entry.Key));
            Assert.Equal((false, true), WingetLocation.Signals(entry, Anki26After()));

            Assert.Equal(SystemProfile, Decide(Anki26After(@"C:\Windows\System32\config\systemprofile\AppData\Local\Programs\Anki\anki.exe,0")).Note);
        }

        // Saha: 32 bit NSIS, kayıt HKU\S-1-5-18'de, yol SYSTEM'in profilinde (WOW64 yönlendirmesiyle SysWOW64): SYSTEM'in profili.
        // Kovan ve yol ayrı ayrı da aynı yeri gösterir.
        [Fact]
        public void Anki2165_Nsis32InSystemsProfile_IsSystemsProfile()
        {
            Assert.Equal(SystemProfile, Decide(Anki2165After()).Note);
            Assert.Equal((false, true), WingetLocation.Signals(new UninstallEntry(UninstallHive.SystemUser, "Anki"), Registry(Array.Empty<UninstallEntry>())));
            var wow64 = new UninstallEntry(UninstallHive.Machine32, "Anki")
            {
                InstallLocation = @"C:\Windows\SysWOW64\config\systemprofile\AppData\Local\Programs\Anki",
            };
            Assert.Equal(SystemProfile, Decide(Registry(new[] { Edge(), Runtime(), wow64 })).Note);
        }

        // Saha: makine geneli MSI (ürün HKLM\SOFTWARE\Classes\Installer\Products'ta, yol Program Files'ta): makine geneli
        [Fact]
        public void SevenZip_PerMachineMsi_IsMachineWide()
        {
            LocationDecision decision = Decide(SevenZipAfter());
            Assert.Equal(Machine, decision.Note);
            Assert.Equal(SevenZipCode, Assert.Single(decision.Entries).Key);
        }

        // Saha: makine geneli taşınabilir paket (HKLM, Program Files\WinGet\Packages): makine geneli
        [Fact]
        public void TelegramPortable_InProgramFilesWinGetPackages_IsMachineWide() => Assert.Equal(Machine, Decide(TelegramAfter()).Note);

        // Yeni ya da sürümü değişen kayıt yok (MSIX, kaldırma kaydı yazmayan yükleyici): tahmin edilmez
        [Fact]
        public void NothingNew_IsUnknown()
        {
            LocationDecision decision = Decide(Before());
            Assert.Equal(Unknown("yeni ya da sürümü değişen kaldırma kaydı yok"), decision.Note);
            Assert.Empty(decision.Entries);
            Assert.Equal("[kurulduğu yer: belirlenemedi (yeni ya da sürümü değişen kaldırma kaydı yok)]", decision.Note);
        }

        // Paket zaten kuruluydu: yükleyici çalışmadı, arada değişen kayıtlar (burada yeni bir Anki bile) pakete ait sayılmaz
        [Theory]
        [InlineData(-1978335189)]   // 0x8A15002B
        [InlineData(-1978335135)]   // 0x8A150061
        public void AlreadyInstalled_IsUnknown_WhateverChanged(int exitCode)
        {
            Assert.Equal(Unknown("paket zaten kuruluydu; yükleyici çalışmadı"), Decide(SevenZipAfter(), exitCode).Note);
            Assert.True(WingetInstall.AlreadyInstalled(exitCode));
            Assert.True(WingetInstall.IsOnThePc(exitCode));
        }

        // Yeniden başlatmanın bitireceği kurulumda (0x8A150109) kayıtlar okunur
        [Fact]
        public void RestartRequired_IsDecidedFromTheRegistry() => Assert.Equal(SystemProfile, Decide(Anki26After(), -1978334967).Note);

        // Yerinde yükseltme: yeni kayıt yok, paketin kaydının sürümü değişti; değişmeyen kayıtlar sayılmaz
        [Fact]
        public void InPlaceUpgrade_UsesTheEntryWhoseVersionChanged()
        {
            InstallRegistrySnapshot before = Registry(new[] { Edge(), Runtime(), Anki2165("2.1.64") });
            LocationDecision decision = WingetLocation.Decide(0, before, Anki2165After());
            Assert.Equal(SystemProfile, decision.Note);
            Assert.Equal("2.1.65", Assert.Single(decision.Entries).DisplayVersion);
        }

        // Aynı anda kendini güncelleyen bir program yalnızca sürümünü değiştirir: yeni kayıt varken sayılmaz
        [Fact]
        public void NewEntries_WinOverVersionChanges()
        {
            InstallRegistrySnapshot after = Registry(new[] { Edge("131.0.2903.51"), Runtime(), Anki26() }, null, new[] { AnkiPacked });
            (List<UninstallEntry> added, List<UninstallEntry> updated) = WingetLocation.Changes(Before(), after);
            Assert.Equal(AnkiCode, Assert.Single(added).Key);
            Assert.Equal("Microsoft Edge", Assert.Single(updated).Key);
            LocationDecision decision = Decide(after);
            Assert.Equal(SystemProfile, decision.Note);
            Assert.Equal(AnkiCode, Assert.Single(decision.Entries).Key);
        }

        // Anahtarın adı büyük/küçük harfe duyarsızdır, kovanı kimliğinin parçasıdır (aynı ad başka kovanda yeni bir kayıttır)
        [Fact]
        public void Changes_MatchKeysWithoutCase_PerHive()
        {
            InstallRegistrySnapshot before = Registry(new[] { new UninstallEntry(UninstallHive.Machine, "{ABC}") { DisplayVersion = "1" } });
            InstallRegistrySnapshot after = Registry(new[]
            {
                new UninstallEntry(UninstallHive.Machine, "{abc}") { DisplayVersion = "1" },
                new UninstallEntry(UninstallHive.SystemUser, "{ABC}") { DisplayVersion = "1" },
            });
            (List<UninstallEntry> added, List<UninstallEntry> updated) = WingetLocation.Changes(before, after);
            Assert.Equal(UninstallHive.SystemUser, Assert.Single(added).Hive);
            Assert.Empty(updated);
        }

        // İşaretler çelişirse belirlenemedi: makine geneli MSI ama dosyalar SYSTEM'in profilinde; SYSTEM'in kaydı ama dosyalar
        // Program Files'ta; ürün iki listede birden; iki yeni kayıt iki ayrı yerde
        private static InstallRegistrySnapshot Conflict(int index) => index switch
        {
            0 => Registry(new[] { new UninstallEntry(UninstallHive.Machine, AnkiCode)
                { WindowsInstaller = true, InstallLocation = @"C:\Windows\System32\config\systemprofile\AppData\Local\Programs\Anki\" } },
                new[] { AnkiPacked }),
            1 => Registry(new[] { new UninstallEntry(UninstallHive.SystemUser, "Foo") { InstallLocation = @"C:\Program Files\Foo" } }),
            2 => Registry(new[] { Anki26() }, new[] { AnkiPacked }, new[] { AnkiPacked }),
            _ => Registry(new[] { Anki2165(), SevenZipAfter().Entries.Last() }, new[] { SevenZipPacked }),
        };

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        public void Conflicts_AreUnknown(int index) =>
            Assert.Equal(Unknown("kayıtlar hem makine genelini hem SYSTEM'in profilini gösteriyor"), Decide(Conflict(index)).Note);

        // İşaret yok: HKLM'deki MSI olmayan kaydın kovanı tek başına bir şey söylemez, yolu Program Files dışında; MSI'ın ürün kaydı
        // bulunamadı ve yol yok (MSI'ın kaldırma kaydının kovanı da sayılmaz)
        [Fact]
        public void NoSignal_IsUnknown()
        {
            string none = Unknown("kaldırma kaydı kurulduğu yeri göstermiyor");
            Assert.Equal(none, Decide(Registry(new[] { new UninstallEntry(UninstallHive.Machine, "Foo")
                { InstallLocation = @"D:\Araçlar\Foo", UninstallString = @"""D:\Araçlar\Foo\uninst.exe"" /S" } })).Note);
            Assert.Equal(none, Decide(Registry(new[] { Anki26() })).Note);
            Assert.Equal(none, Decide(Registry(new[] { new UninstallEntry(UninstallHive.Machine32, "Bar") })).Note);
        }

        // Kayıt defteri kurulumdan önce ya da sonra okunamadı: belirlenemedi
        [Fact]
        public void UnreadableRegistry_IsUnknown()
        {
            string unreadable = Unknown("kayıt defteri okunamadı");
            Assert.Equal(unreadable, WingetLocation.Decide(0, InstallRegistrySnapshot.Unreadable("erişim reddedildi"), Anki26After()).Note);
            Assert.Equal(unreadable, WingetLocation.Decide(0, Before(), InstallRegistrySnapshot.Unreadable("erişim reddedildi")).Note);
        }

        // Yol işaretleri InstallLocation, UninstallString ve DisplayIcon'dan: tırnak, bağımsız değişken ve simge sırası (",0")
        // olabilir; büyük/küçük harf fark etmez
        [Theory]
        [InlineData(@"C:\Program Files\7-Zip\", true, false)]
        [InlineData(@"D:\Program Files (x86)\Foo\foo.exe,0", true, false)]
        [InlineData(@"""C:\PROGRA~1\Foo\uninst.exe"" /S", true, false)]
        [InlineData(@"c:\program files\winget\packages\x", true, false)]
        [InlineData(@"C:\Windows\System32\config\systemprofile\AppData\Local\Programs\Anki", false, true)]
        [InlineData(@"C:\WINDOWS\SysWOW64\config\systemprofile\AppData\Roaming\Foo\foo.exe,0", false, true)]
        [InlineData(@"""C:\Windows\Sysnative\config\systemprofile\AppData\Local\Foo\u.exe"" --uninstall", false, true)]
        [InlineData(@"C:\Windows\System32\config\systemprofile", false, true)]
        [InlineData(@"C:\ProgramData\Foo", false, false)]
        [InlineData(@"C:\Users\ayse\AppData\Local\Programs\Foo", false, false)]
        [InlineData(@"C:\Program FilesX\Foo", false, false)]
        [InlineData(@"C:\Windows\System32\config\systemprofileX\Foo", false, false)]
        [InlineData(@"C:\Windows\Installer\{7AD362E6-020E-4F25-AD49-94A408DC5CA5}\anki.ico", false, false)]
        [InlineData("MsiExec.exe /X" + AnkiCode, false, false)]
        public void PathSignals(string path, bool machineWide, bool systemProfile)
        {
            var registry = Registry(Array.Empty<UninstallEntry>());
            Assert.Equal((machineWide, systemProfile), WingetLocation.Signals(new UninstallEntry(UninstallHive.Machine, "Foo") { InstallLocation = path }, registry));
            Assert.Equal((machineWide, systemProfile), WingetLocation.Signals(new UninstallEntry(UninstallHive.Machine32, "Foo") { UninstallString = path }, registry));
            Assert.Equal((machineWide, systemProfile), WingetLocation.Signals(new UninstallEntry(UninstallHive.Machine, "Foo") { DisplayIcon = path }, registry));
        }

        // MSI'ın işareti yalnızca WindowsInstaller = 1 ve ürün kodu anahtarıyla: aynı ad MSI olmayan kayıtta ürün listesine bakılmaz
        [Fact]
        public void ProductRegistration_CountsOnlyForWindowsInstallerEntries()
        {
            InstallRegistrySnapshot registry = Registry(Array.Empty<UninstallEntry>(), new[] { SevenZipPacked }, new[] { AnkiPacked });
            Assert.Equal((false, false), WingetLocation.Signals(new UninstallEntry(UninstallHive.Machine, AnkiCode), registry));
            Assert.Equal((false, true), WingetLocation.Signals(new UninstallEntry(UninstallHive.Machine, AnkiCode) { WindowsInstaller = true }, registry));
            Assert.Equal((true, false), WingetLocation.Signals(new UninstallEntry(UninstallHive.Machine32, SevenZipCode.ToLowerInvariant()) { WindowsInstaller = true }, registry));
        }
    }
}
