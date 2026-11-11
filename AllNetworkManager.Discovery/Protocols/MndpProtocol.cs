namespace AllNetworkManager.Discovery.Protocols;

using AllNetworkManager.Core.Discovery;
using AllNetworkManager.Core.Devices;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

public class MndpProtocol : IDiscoveryProtocol
{
    private readonly ILogger<MndpProtocol> _logger;
    private const int MndpPort = 5678;
    private const string MndpMulticast = "224.0.0.56";
    private const int DefaultListenWindowSec = 5; // Increased from 3 to catch slow responders
    private const int ReceiveTimeoutMs = 800; // Increased from 500ms for more reliable reception

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
            var localIp = GetLocalIpForSubnet(subnet);
            if (localIp == null)
            {
                _logger.LogDebug("No local IP found for subnet {Subnet}", subnet);
                return devices;
            }

            var localMac = GetLocalMacAddress(localIp);
            
            using var udpClient = new UdpClient();
            udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            var localEp = new IPEndPoint(localIp, 0);
            udpClient.Client.Bind(localEp);
            udpClient.EnableBroadcast = true;

            // Send to both multicast (preferred, works across VLANs with proper config) and broadcast
            var multicastEp = new IPEndPoint(IPAddress.Parse(MndpMulticast), MndpPort);
            var broadcastEp = new IPEndPoint(IPAddress.Broadcast, MndpPort);
            var discoverPacket = CreateMndpDiscover(localMac);
            
            // Send to multicast first (preferred) - send multiple times for reliability
            for (int i = 0; i < 2; i++)
            {
                await udpClient.SendAsync(discoverPacket, discoverPacket.Length, multicastEp);
                await Task.Delay(50, ct);
            }
            
            // Also send broadcast for compatibility
            await udpClient.SendAsync(discoverPacket, discoverPacket.Length, broadcastEp);

            var endTime = DateTime.UtcNow.AddSeconds(DefaultListenWindowSec);
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
                    await Task.Delay(50, ct); // Reduced from 100ms for faster response processing
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
            var localIp = GetLocalIpForTarget(ipAddress);
            
            using var udpClient = new UdpClient();
            udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            
            if (localIp != null)
            {
                udpClient.Client.Bind(new IPEndPoint(localIp, 0));
                // Set multicast interface for proper routing
                var localEp = udpClient.Client.LocalEndPoint as IPEndPoint;
                if (localEp != null)
                {
                    udpClient.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface,
                        localEp.Address.GetAddressBytes());
                }
            }
            else
            {
                udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
            }
            
            udpClient.Client.ReceiveTimeout = ReceiveTimeoutMs;

            var remoteEp = new IPEndPoint(IPAddress.Parse(ipAddress), MndpPort);
            var localMac = localIp != null ? GetLocalMacAddress(localIp) : null;
            var discoverPacket = CreateMndpDiscover(localMac);
            
            // Send probe twice for reliability
            for (int i = 0; i < 2; i++)
            {
                await udpClient.SendAsync(discoverPacket, discoverPacket.Length, remoteEp);
                await Task.Delay(50, ct);
            }

            var endTime = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < endTime && !ct.IsCancellationRequested)
            {
                try
                {
                    var result = await udpClient.ReceiveAsync();
                    return ParseMndpResponse(result.Buffer, ipAddress);
                }
                catch (OperationCanceledException) { break; }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
                {
                    // Timeout, try again if time remains
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Error receiving MNDP probe response");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "MNDP probe failed for {Ip}", ipAddress);
        }
        return null;
    }

    private IPAddress? GetLocalIpForSubnet(string subnet)
    {
        try
        {
            var parts = subnet.Split('/');
            if (parts.Length != 2) return null;
            
            var baseIp = IPAddress.Parse(parts[0]);
            var prefixLen = int.Parse(parts[1]);
            
            // Prefer interface with smallest subnet match (most specific)
            IPAddress? bestMatch = null;
            int bestPrefixLen = -1;
            
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                
                var ipProps = ni.GetIPProperties();
                foreach (var uip in ipProps.UnicastAddresses)
                {
                    if (uip.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IsInSameSubnet(uip.Address, baseIp, prefixLen))
                    {
                        var interfacePrefix = uip.IPv4Mask != null ? GetPrefixLength(uip.IPv4Mask) : 32;
                        if (interfacePrefix > bestPrefixLen)
                        {
                            bestPrefixLen = interfacePrefix;
                            bestMatch = uip.Address;
                        }
                    }
                }
            }
            
            return bestMatch;
        }
        catch { }
        return null;
    }

    private IPAddress? GetLocalIpForTarget(string targetIp)
    {
        try
        {
            var target = IPAddress.Parse(targetIp);
            
            // Prefer interface with smallest subnet match (most specific)
            IPAddress? bestMatch = null;
            int bestPrefixLen = -1;
            
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                
                var ipProps = ni.GetIPProperties();
                foreach (var uip in ipProps.UnicastAddresses)
                {
                    if (uip.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (uip.IPv4Mask == null) continue;
                    
                    var interfacePrefix = GetPrefixLength(uip.IPv4Mask);
                    // Check if target is in same subnet as this interface
                    if (IsInSameSubnet(target, uip.Address, interfacePrefix))
                    {
                        if (interfacePrefix > bestPrefixLen)
                        {
                            bestPrefixLen = interfacePrefix;
                            bestMatch = uip.Address;
                        }
                    }
                }
            }
            
            return bestMatch;
        }
        catch { }
        return null;
    }

    private int GetPrefixLength(IPAddress mask)
    {
        var bytes = mask.GetAddressBytes();
        int prefix = 0;
        foreach (var b in bytes)
        {
            prefix += CountBits(b);
        }
        return prefix;
    }

    private int CountBits(byte b)
    {
        // Use built-in popcount for efficiency
        return System.Numerics.BitOperations.PopCount((uint)b);
    }

    private bool IsInSameSubnet(IPAddress ip1, IPAddress ip2, int prefixLen)
    {
        var b1 = ip1.GetAddressBytes();
        var b2 = ip2.GetAddressBytes();
        
        for (int i = 0; i < prefixLen; i++)
        {
            int byteIndex = i / 8;
            int bitIndex = 7 - (i % 8);
            if (((b1[byteIndex] >> bitIndex) & 1) != ((b2[byteIndex] >> bitIndex) & 1))
                return false;
        }
        return true;
    }

    private byte[]? GetLocalMacAddress(IPAddress localIp)
    {
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                
                var ipProps = ni.GetIPProperties();
                foreach (var uip in ipProps.UnicastAddresses)
                {
                    if (uip.Address.Equals(localIp))
                    {
                        var macBytes = ni.GetPhysicalAddress().GetAddressBytes();
                        if (macBytes.Length == 6) return macBytes;
                    }
                }
            }
        }
        catch { }
        return null;
    }

    private byte[] CreateMndpDiscover(byte[]? localMac = null)
    {
        var packet = new byte[32];
        packet[0] = 0x01; // Version
        packet[1] = 0x00; // Type: Discover
        packet[2] = 0x00; // Sequence
        packet[3] = 0x00; // Reserved
        
        // Include local MAC address if available (bytes 4-9)
        if (localMac != null && localMac.Length == 6)
        {
            Array.Copy(localMac, 0, packet, 4, 6);
        }
        // Otherwise leave as zeros (broadcast discover)
        
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
            // Efficient MAC formatting without BitConverter.ToString + Replace
            var mac = data.Length >= offset + 6 
                ? string.Join(':', data.Skip(offset).Take(6).Select(b => b.ToString("X2")))
                : "";
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
            
            // Additional TLVs that might be present
            var boardSeries = GetString(tlvs.GetValueOrDefault<byte, byte[]>(0x09));
            var architecture = GetString(tlvs.GetValueOrDefault<byte, byte[]>(0x0A));

            // Better model identification combining platform + board + boardSeries
            var modelParts = new List<string>();
            if (!string.IsNullOrWhiteSpace(platform)) modelParts.Add(platform);
            if (!string.IsNullOrWhiteSpace(board)) modelParts.Add(board);
            if (!string.IsNullOrWhiteSpace(boardSeries)) modelParts.Add(boardSeries);
            var model = modelParts.Count > 0 ? string.Join(" ", modelParts) : "RouterOS";

            return new DiscoveredDevice(
                IpAddress: sourceIp,
                Port: 8728,
                Manufacturer: "MikroTik",
                Model: model,
                MacAddress: mac,
                FirmwareVersion: swVersion ?? versionStr,
                HardwareVersion: hwVersion,
                Properties: new Dictionary<string, object>
                {
                    ["identity"] = identity ?? "",
                    ["platform"] = platform ?? "",
                    ["board"] = board ?? "",
                    ["boardSeries"] = boardSeries ?? "",
                    ["architecture"] = architecture ?? "",
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