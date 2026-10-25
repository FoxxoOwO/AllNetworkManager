namespace AllNetworkManager.Core.Network;

using System.Net;
using System.Net.Sockets;

/// <summary>
/// Vendor-independent Wake-on-LAN: UDP magic packet on ports 7 and 9, broadcast to the local subnet.
/// </summary>
public static class WakeOnLan
{
    public static async Task<bool> SendAsync(string macAddress, IPAddress? broadcast = null, CancellationToken ct = default)
    {
        var mac = ParseMac(macAddress);
        if (mac == null) return false;

        var packet = BuildPacket(mac);
        var target = broadcast ?? IPAddress.Broadcast;

        using var udp = new UdpClient();
        udp.EnableBroadcast = true;

        var sent = 0;
        foreach (var port in new[] { 9, 7 })
        {
            try
            {
                await udp.SendAsync(packet.AsMemory(), new IPEndPoint(target, port), ct).ConfigureAwait(false);
                sent++;
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // one of the two ports failing is not fatal
            }
        }

        return sent > 0;
    }

    public static byte[] BuildPacket(byte[] mac)
    {
        var packet = new byte[6 + 16 * 6];
        for (int i = 0; i < 6; i++) packet[i] = 0xFF;
        for (int i = 0; i < 16; i++) Buffer.BlockCopy(mac, 0, packet, 6 + i * 6, 6);
        return packet;
    }

    public static byte[]? ParseMac(string macAddress)
    {
        if (string.IsNullOrWhiteSpace(macAddress)) return null;

        var clean = macAddress.Replace(":", "").Replace("-", "").Replace(".", "").Trim();
        if (clean.Length != 12) return null;

        try
        {
            return Convert.FromHexString(clean);
        }
        catch
        {
            return null;
        }
    }
}
