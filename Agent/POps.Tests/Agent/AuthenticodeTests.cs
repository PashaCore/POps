using System;
using System.IO;
using POpsAgent;
using Xunit;

namespace POps.Tests.Agent
{
    // Tepsi doğrulamasının imza denetimi (yalnızca dosya okur)
    public class AuthenticodeTests : TestBase
    {
        [Fact]
        public void UnsignedBinary_IsUnsigned() =>
            Assert.Equal(Authenticode.Result.Unsigned, Authenticode.Verify(typeof(AuthenticodeTests).Assembly.Location));

        [Fact]
        public void SignedBinary_IsValid()
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
            if (!File.Exists(path)) return; // .NET SDK başka bir yere kuruluysa
            Assert.Equal(Authenticode.Result.Valid, Authenticode.Verify(path));
        }

        [Fact]
        public void SignedBinaryWithAChangedByte_IsInvalid()
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
            if (!File.Exists(path)) return;
            byte[] bytes = File.ReadAllBytes(path);
            bytes[bytes.Length / 3] ^= 0xFF;
            string tampered = Path.Combine(TestEnvironment.NewDir("sig"), "tampered.exe");
            File.WriteAllBytes(tampered, bytes);
            Assert.Equal(Authenticode.Result.Invalid, Authenticode.Verify(tampered));
        }
    }
}
