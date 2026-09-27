using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using POpsAgent;
using Xunit;

namespace POps.Tests.Agent
{
    public class SecureStoreTests : TestBase
    {
        [Fact]
        public void Directory_IsProtectedAndReappliedOnEveryStart()
        {
            SecureStore.Dir = Path.Combine(TestEnvironment.NewDir("sec"), "secure");
            SecureStore.EnsureDirectory();
            Assert.True(SecureStore.IsLockedDown(new DirectoryInfo(SecureStore.Dir).GetAccessControl()));
            SecureStore.EnsureDirectory();
            Assert.True(SecureStore.IsLockedDown(new DirectoryInfo(SecureStore.Dir).GetAccessControl()));
        }

        [Fact]
        public void WriteProtected_ReplacesAtomically_AndStaysLocked()
        {
            string path = Path.Combine(TestEnvironment.NewDir("sec"), "x.secret");
            SecureStore.WriteProtected(path, "first");
            SecureStore.WriteProtected(path, "second");
            Assert.Equal("second", SecureStore.Read(path));
            Assert.False(File.Exists(path + ".tmp"));
            Assert.True(SecureStore.IsLockedDown(new FileInfo(path).GetAccessControl()));
        }

        [Fact]
        public void IsLockedDown_DetectsAnExtraAce()
        {
            string path = Path.Combine(TestEnvironment.NewDir("sec"), "x.secret");
            SecureStore.WriteProtected(path, "v");
            var file = new FileInfo(path);
            FileSecurity sec = file.GetAccessControl();
            sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.Read, AccessControlType.Allow));
            file.SetAccessControl(sec);
            Assert.False(SecureStore.IsLockedDown(file.GetAccessControl()));
        }

        [Fact]
        public void TrustedOwnerCheck_IgnoresFilesOwnedByOthers()
        {
            // Test kullanıcısı "servis hesabı"; başka bir sahip (ör. Users grubu) güvenilmez sayılır
            string path = Path.Combine(TestEnvironment.NewDir("sec"), "planted.secret");
            File.WriteAllText(path, "planted");
            Assert.Equal("planted", SecureStore.Read(path, requireTrustedOwner: true));

            SecurityIdentifier original = SecureStore.SystemSid;
            try
            {
                SecureStore.SystemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
                if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
                    Assert.Null(SecureStore.Read(path, requireTrustedOwner: true));
            }
            finally { SecureStore.SystemSid = original; }
        }
    }
}
