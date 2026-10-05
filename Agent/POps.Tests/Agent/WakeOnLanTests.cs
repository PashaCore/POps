using System.Linq;
using POpsAgent;
using Xunit;

namespace POps.Tests.Agent
{
    // wake_peer: sihirli paket yapısı ve MAC doğrulaması (gönderim ağa çıkmadan sınanır)
    public class WakeOnLanTests : TestBase
    {
        [Theory]
        [InlineData("00:1A:2b:3C:4d:5E")]
        [InlineData("00-1A-2B-3C-4D-5E")]
        [InlineData("001A.2B3C.4D5E")]
        [InlineData("001A2B3C4D5E")]
        [InlineData(" 00:1A:2B:3C:4D:5E ")]
        public void MagicPacket_SixFFsThenTheMacSixteenTimes(string mac)
        {
            byte[] packet = WakeOnLan.BuildMagicPacket(mac);
            Assert.Equal(102, packet.Length);
            Assert.All(packet.Take(6), b => Assert.Equal(0xFF, b));
            byte[] expected = { 0x00, 0x1A, 0x2B, 0x3C, 0x4D, 0x5E };
            for (int i = 1; i <= 16; i++) Assert.Equal(expected, packet.Skip(i * 6).Take(6).ToArray());
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("00:1A:2B:3C:4D")]
        [InlineData("00:1A:2B:3C:4D:5E:6F")]
        [InlineData("zz:1A:2B:3C:4D:5E")]
        [InlineData("+0:1A:2B:3C:4D:5E")]
        public void InvalidMac_NoPacket(string mac) => Assert.Null(WakeOnLan.BuildMagicPacket(mac));
    }
}
