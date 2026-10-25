namespace AllNetworkManager.Discovery.Protocols;

using AllNetworkManager.Core.Discovery;
using AllNetworkManager.Core.Devices;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Sockets;

public class MndpProtocol : IDiscoveryProtocol
{
    private readonly ILogger<MndpProtocol> _logger;
    private const int MndpPort = 5678;

    public string ProtocolName => "MNDP (MikroTik Neighbor Discovery Protocol)";

    public MndpProtocol(ILogger<MndpProtocol> logger)
    {
        _logger = logger;
    }

    public bool SupportsPort(int port) => port == MndpPort || port == 8728 || port == 8729 || port == 8291;

    public async Task<IEnumerable<DiscoveredDevice>> ScanAsync(string subnet, CancellationToken ct = default)
    {
        var devices = new List<DiscoveredDevice>();

        try
        {
            using var udpClient = new UdpClient();
            udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
            udpClient.EnableBroadcast = true;

            var broadcastEp = new IPEndPoint(IPAddress.Broadcast, MndpPort);
            var discoverPacket = CreateMndpDiscover();
            
            await udpClient.SendAsync(discoverPacket, discoverPacket.Length, broadcastEp);

            var endTime = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < endTime && !ct.IsCancellationRequested)
            {
                try
                {
                    if (udpClient.Available > 0)
                    {
                        var result = await udpClient.ReceiveAsync();
                        var device = ParseMndpResponse(result.Buffer, result.RemoteEndPoint.Address.ToString());
                        if (device != null)
                            devices.Add(device);
                    }
                    await Task.Delay(100, ct);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Error receiving MNDP response");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MNDP scan failed");
        }

        return devices;
    }

    public async Task<DiscoveredDevice?> ProbeAsync(string ipAddress, int port, CancellationToken ct = default)
    {
        try
        {
            using var udpClient = new UdpClient();
            udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
            udpClient.Client.ReceiveTimeout = 2000;

            var remoteEp = new IPEndPoint(IPAddress.Parse(ipAddress), MndpPort);
            var discoverPacket = CreateMndpDiscover();
            
            await udpClient.SendAsync(discoverPacket, discoverPacket.Length, remoteEp);

            var result = await udpClient.ReceiveAsync();
            return ParseMndpResponse(result.Buffer, ipAddress);
        }
        catch { }
        return null;
    }

    private byte[] CreateMndpDiscover()
    {
        var packet = new byte[32];
        packet[0] = 0x01; // Version
        packet[1] = 0x00; // Type: Discover
        packet[2] = 0x00; // Sequence
        packet[3] = 0x00; // Reserved
        // MAC address would go here, but for discover we leave as zeros
        return packet;
    }

    private DiscoveredDevice? ParseMndpResponse(byte[] data, string sourceIp)
    {
        try
        {
            if (data.Length < 24) return null;

            var version = data[0];
            var type = data[1];
            if (type != 0x02) return null; // Not a reply

            var offset = 4;
            var mac = BitConverter.ToString(data, offset, 6).Replace('-', ':');
            offset += 6;

            var tlvs = new Dictionary<byte, byte[]>();
            while (offset + 3 < data.Length)
            {
                var tlvType = data[offset];
                var tlvLen = BitConverter.ToUInt16(data, offset + 1);
                if (offset + 3 + tlvLen > data.Length) break;
                
                var tlvData = new byte[tlvLen];
                Array.Copy(data, offset + 3, tlvData, 0, tlvLen);
                tlvs[tlvType] = tlvData;
                offset += 3 + tlvLen;
            }

            var identity = GetString(tlvs.GetValueOrDefault<byte, byte[]>(0x01));
            var versionStr = GetString(tlvs.GetValueOrDefault<byte, byte[]>(0x02));
            var platform = GetString(tlvs.GetValueOrDefault<byte, byte[]>(0x03));
            var board = GetString(tlvs.GetValueOrDefault<byte, byte[]>(0x04));
            var uptime = GetUInt32(tlvs.GetValueOrDefault<byte, byte[]>(0x05));
            var swVersion = GetString(tlvs.GetValueOrDefault<byte, byte[]>(0x07));
            var hwVersion = GetString(tlvs.GetValueOrDefault<byte, byte[]>(0x08));

            return new DiscoveredDevice(
                IpAddress: sourceIp,
                Port: 8728,
                Manufacturer: "MikroTik",
                Model: $"{platform} {board}".Trim(),
                MacAddress: mac,
                FirmwareVersion: swVersion ?? versionStr,
                HardwareVersion: hwVersion,
                Properties: new Dictionary<string, object>
                {
                    ["identity"] = identity ?? "",
                    ["platform"] = platform ?? "",
                    ["board"] = board ?? "",
                    ["uptime"] = uptime ?? 0,
                    ["version"] = versionStr ?? "",
                    ["mndp_version"] = version
                },
                WebInterfaceUrl: new Uri($"http://{sourceIp}"),
                ApiEndpoint: new Uri($"http://{sourceIp}:8728"),
                DiscoveryProtocol: "MNDP"
            );
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to parse MNDP response");
        }
        return null;
    }

    private string? GetString(byte[]? data)
    {
        if (data == null || data.Length == 0) return null;
        return System.Text.Encoding.ASCII.GetString(data).TrimEnd('\0');
    }

    private uint? GetUInt32(byte[]? data)
    {
        if (data == null || data.Length < 4) return null;
        return BitConverter.ToUInt32(data, 0);
    }
}