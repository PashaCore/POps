using System;
using System.Net;
using System.Net.Sockets;

#nullable disable

namespace POpsAgent
{
    // Wake-on-LAN sihirli paketi (P2P uyandırma): yalnızca ajanın kullandığı yardımcı, ortak kütüphaneye taşınmadı.
    public static class WakeOnLan
    {
        public static void Send(string macAddress)
        {
            try
            {
                byte[] packet = BuildMagicPacket(macAddress);
                if (packet == null)
                {
                    POpsHelpers.Log("HELPERS", $"WOL Hatası: Geçersiz MAC adresi formatı ({macAddress})", true);
                    return;
                }

                using UdpClient client = new UdpClient();
                client.EnableBroadcast = true;
                client.Send(packet, packet.Length, new IPEndPoint(IPAddress.Broadcast, 9));
                POpsHelpers.Log("HELPERS", $"WOL Sihirli Paketi fırlatıldı: {macAddress}");
            }
            catch (Exception ex)
            {
                POpsHelpers.Log("HELPERS", $"WOL Gönderim hatası ({macAddress}): {ex.Message}", true);
            }
        }

        // 6 x 0xFF + MAC'in 16 tekrarı (102 bayt). MAC ':' '-' '.' ayraçlı ya da ayraçsız 12 onaltılık hane; değilse null.
        public static byte[] BuildMagicPacket(string macAddress)
        {
            string cleanMac = (macAddress ?? "").Replace(":", "").Replace("-", "").Replace(".", "").Trim();
            if (cleanMac.Length != 12) return null;

            byte[] macBytes = new byte[6];
            for (int i = 0; i < 6; i++)
            {
                if (!byte.TryParse(cleanMac.Substring(i * 2, 2), System.Globalization.NumberStyles.HexNumber, null, out macBytes[i])) return null;
            }

            byte[] packet = new byte[102];
            for (int i = 0; i < 6; i++) packet[i] = 0xFF;
            for (int i = 1; i <= 16; i++)
            {
                for (int j = 0; j < 6; j++)
                {
                    packet[i * 6 + j] = macBytes[j];
                }
            }
            return packet;
        }
    }
}
