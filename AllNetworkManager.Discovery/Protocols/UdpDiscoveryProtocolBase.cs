namespace AllNetworkManager.Discovery.Protocols;

using AllNetworkManager.Core.Devices;
using AllNetworkManager.Core.Discovery;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Sockets;

/// <summary>
/// Shared engine for the vendor broadcast/multicast discovery protocols (the class of protocols
/// UniversalScanner documents): one datagram out, a stream of datagrams back, each parsed into a device.
/// </summary>
public abstract class UdpDiscoveryProtocolBase : IDiscoveryProtocol
{
    private const int ReceiveBufferSize = 8192;

    protected UdpDiscoveryProtocolBase(ILogger logger) => Logger = logger;

    protected ILogger Logger { get; }

    public abstract string ProtocolName { get; }

    /// <summary>Manufacturer name reported to the inventory; defaults to the protocol name.</summary>
    protected virtual string VendorName => ProtocolName;

    /// <summary>UDP port the request is sent to.</summary>
    protected abstract int RequestPort { get; }

    /// <summary>Fixed reply port, when the device does not answer to the sender's port.</summary>
    protected virtual int? ReplyPort => null;

    /// <summary>Where the request is sent. Multicast groups are sent from every local address.</summary>
    protected virtual IPAddress[] RequestAddresses => new[] { IPAddress.Broadcast };

    /// <summary>Serialised discovery request.</summary>
    protected abstract byte[] BuildRequest();

    /// <summary>Variant for protocols that must tell the device where to answer.</summary>
    protected virtual byte[] BuildRequest(int localPort) => BuildRequest();

    /// <summary>Parses one reply datagram; add zero or one device to <paramref name="found"/>.</summary>
    protected abstract void Parse(byte[] data, IPEndPoint from, List<DiscoveredDevice> found);

    /// <summary>How long to keep listening after the request went out.</summary>
    protected virtual TimeSpan ListenWindow => TimeSpan.FromSeconds(3);

    public bool SupportsPort(int port) => port == RequestPort || port == ReplyPort;

    public async Task<IEnumerable<DiscoveredDevice>> ScanAsync(string subnet, CancellationToken ct = default)
        => await RunAsync(null, ct).ConfigureAwait(false);

    public async Task<DiscoveredDevice?> ProbeAsync(string ipAddress, int port, CancellationToken ct = default)
    {
        if (!IPAddress.TryParse(ipAddress, out var target)) return null;

        var results = await RunAsync(new IPEndPoint(target, RequestPort), ct).ConfigureAwait(false);
        return results.FirstOrDefault();
    }

    /// <summary>
    /// Feeds one captured reply through the parser. Used by the tests and to replay packet captures
    /// taken from a device that is not reachable from the build machine.
    /// </summary>
    public IReadOnlyList<DiscoveredDevice> TryParseReply(byte[] reply, IPEndPoint from)
    {
        var found = new List<DiscoveredDevice>();
        Parse(reply, from, found);
        return found;
    }

    private async Task<IReadOnlyList<DiscoveredDevice>> RunAsync(IPEndPoint? unicastTarget, CancellationToken ct)
    {
        var found = new List<DiscoveredDevice>();

        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
            {
                EnableBroadcast = true,
                ReceiveTimeout = 500
            };

            socket.Bind(new IPEndPoint(IPAddress.Any, ReplyPort ?? 0));

            var request = BuildRequest(((IPEndPoint)socket.LocalEndPoint!).Port);

            foreach (var (address, localAddress) in EnumerateSends(unicastTarget))
            {
                try
                {
                    if (address.AddressFamily == AddressFamily.InterNetwork &&
                        IsMulticast(address) && localAddress != null)
                    {
                        socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface,
                            localAddress.GetAddressBytes());
                        socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
                    }

                    await socket.SendToAsync(request, SocketFlags.None, new IPEndPoint(address, RequestPort), ct)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Logger.LogDebug(ex, "{Protocol}: sending the request to {Address} failed", ProtocolName, address);
                }
            }

            await ReceiveLoopAsync(socket, found, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "{Protocol}: discovery failed", ProtocolName);
        }

        return found;
    }

    private IEnumerable<(IPAddress Target, IPAddress? Local)> EnumerateSends(IPEndPoint? unicastTarget)
    {
        if (unicastTarget != null)
        {
            yield return (unicastTarget.Address, null);
            yield break;
        }

        var localAddresses = LocalAddresses();

        foreach (var target in RequestAddresses)
        {
            if (IsMulticast(target) || target.Equals(IPAddress.Broadcast))
            {
                // one datagram per interface, so a request leaves through every card
                foreach (var local in localAddresses)
                    yield return (target, local);
            }
            else
            {
                yield return (target, null);
            }
        }
    }

    private async Task ReceiveLoopAsync(Socket socket, List<DiscoveredDevice> found, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + ListenWindow;
        var buffer = new byte[ReceiveBufferSize];

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(400));

            try
            {
                var result = await socket.ReceiveFromAsync(buffer, SocketFlags.None,
                        new IPEndPoint(IPAddress.Any, 0), timeout.Token)
                    .ConfigureAwait(false);

                var data = new byte[result.ReceivedBytes];
                Buffer.BlockCopy(buffer, 0, data, 0, result.ReceivedBytes);

                Parse(data, (IPEndPoint)result.RemoteEndPoint, found);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // nothing arrived in this slice, keep waiting until the window closes
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset
                                                 or SocketError.MessageSize)
            {
                // ICMP port unreachable from a host without the service, or an oversized datagram
            }
        }
    }

    private static List<IPAddress> LocalAddresses()
    {
        var addresses = new List<IPAddress>();

        try
        {
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;

                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily == AddressFamily.InterNetwork)
                        addresses.Add(unicast.Address);
                }
            }
        }
        catch
        {
            // no interfaces visible: fall back to a single unbound send
        }

        if (addresses.Count == 0) addresses.Add(IPAddress.Any);
        return addresses;
    }

    // ------------------------------------------------------------- shared helpers

    /// <summary>Cuts a fixed-width NUL/length delimited ASCII field out of a reply.</summary>
    protected static string FixedString(byte[] data, int offset, int length)
    {
        if (offset < 0 || offset >= data.Length) return "";

        var end = Math.Min(offset + length, data.Length);
        var span = data.AsSpan(offset, end - offset);

        var nul = span.IndexOf((byte)0);
        if (nul >= 0) span = span[..nul];

        return System.Text.Encoding.UTF8.GetString(span).Trim('\0', ' ', '\r', '\n');
    }

    protected static bool TryIPv4(byte[] data, int offset, out string address)
    {
        address = "";
        if (offset < 0 || offset + 4 > data.Length) return false;

        address = $"{data[offset]}.{data[offset + 1]}.{data[offset + 2]}.{data[offset + 3]}";
        return !address.StartsWith("0.0.0.0");
    }

    protected static string Mac(byte[] data, int offset)
    {
        if (offset < 0 || offset + 6 > data.Length) return "";
        return string.Join(':', data.Skip(offset).Take(6).Select(b => b.ToString("X2")));
    }

    protected DiscoveredDevice Device(string ipAddress, string model, string serial, string mac,
        string? firmware, Dictionary<string, object>? properties = null)
    {
        var port = RequestPort;

        return new DiscoveredDevice(
            IpAddress: ipAddress,
            Port: port,
            Manufacturer: VendorName,
            Model: model,
            MacAddress: string.IsNullOrWhiteSpace(mac) ? null : mac,
            FirmwareVersion: string.IsNullOrWhiteSpace(firmware) ? null : firmware,
            HardwareVersion: null,
            SerialNumber: string.IsNullOrWhiteSpace(serial) ? null : serial,
            Type: GuessType(model),
            Properties: properties,
            WebInterfaceUrl: null,
            ApiEndpoint: null,
            DiscoveryProtocol: ProtocolName);
    }

    private static DeviceType GuessType(string model)
    {
        var text = model ?? "";
        if (text.Contains("CAM", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("IPC", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("NVR", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("DOME", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("BULLET", StringComparison.OrdinalIgnoreCase))
            return DeviceType.Camera;

        if (text.Contains("SW", StringComparison.OrdinalIgnoreCase)) return DeviceType.Switch;
        return DeviceType.Unknown;
    }

    private static bool IsMulticast(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return b[0] is >= 224 and <= 239;
    }
}