using System;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading.Tasks;
using POps.Shared;
using Xunit;

namespace POps.Tests.Agent
{
    // 3) Tepsi, borunun servise ait olduğunu sahibinden anlar (L13)
    [Collection(MachineCollection.Name)]
    public class PipeOwnerTests : TestBase
    {
        [Fact]
        public void OnlySystemOrAdministratorsAreTrusted()
        {
            Assert.True(PipeOwner.IsTrustedOwner(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)));
            Assert.True(PipeOwner.IsTrustedOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)));
            Assert.False(PipeOwner.IsTrustedOwner(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null)));
            Assert.False(PipeOwner.IsTrustedOwner(WindowsIdentity.GetCurrent().User));
            Assert.False(PipeOwner.IsTrustedOwner(null));
        }

        // Servisin borusuyla aynı izin düzeniyle (SYSTEM ve Administrators tam, etkileşimli kullanıcı ReadWrite):
        // istemci sahibi okuyabilmeli; test kullanıcısının açtığı boru güvenilir sayılmamalı
        [Fact]
        public async Task OwnerIsReadThroughTheClientConnection()
        {
            string name = "POpsTest_" + Guid.NewGuid().ToString("N");
            var ps = new PipeSecurity();
            ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
            ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
            ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null), PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));
            // CI oturumu etkileşimli olmayabilir: bağlanabilmek için test kullanıcısına da aynı hak
            ps.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User, PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));

            using NamedPipeServerStream server = NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, ps);
            Task accept = server.WaitForConnectionAsync();
            using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(5000);
            await accept;

            var actualOwner = (SecurityIdentifier)server.GetAccessControl().GetOwner(typeof(SecurityIdentifier));
            bool trusted = PipeOwner.Check(client, out string owner);
            Assert.Equal(actualOwner.Value, owner);
            Assert.Equal(PipeOwner.IsTrustedOwner(actualOwner), trusted);
        }
    }
}
