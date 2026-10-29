namespace AllNetworkManager.Discovery.Protocols.Mdns;

using AllNetworkManager.Core.Devices;
using AllNetworkManager.Core.Discovery;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Sockets;

/// <summary>
/// DNS-SD browsing over mDNS: asks for PTR records of the given services on 224.0.0.251:5353, then joins the
/// PTR/SRV/TXT/A answers into one device per service instance.
/// </summary>
public abstract class MdnsServiceDiscoveryProtocol : IDiscoveryProtocol
{
    private const string MulticastAddress = "224.0.0.251";
    private const int MdnsPort = 5353;

    private readonly ILogger _logger;

    protected MdnsServiceDiscoveryProtocol(ILogger logger) => _logger = logger;

    public abstract string ProtocolName { get; }

    /// <summary>DNS-SD service types, e.g. <c>_axis-video._tcp.local</c>.</summary>
    protected abstract string[] Services { get; }

    protected virtual string VendorName => ProtocolName;

    public bool SupportsPort(int port) => port == MdnsPort;

    /// <summary>Turns one assembled instance into a device; return null to ignore it.</summary>
    protected abstract DiscoveredDevice? ToDevice(MdnsServiceInstance instance);

    public async Task<IEnumerable<DiscoveredDevice>> ScanAsync(string subnet, CancellationToken ct = default)
        => await BrowseAsync(ct).ConfigureAwait(false);

    public async Task<DiscoveredDevice?> ProbeAsync(string ipAddress, int port, CancellationToken ct = default)
    {
        var devices = await BrowseAsync(ct).ConfigureAwait(false);
        return devices.FirstOrDefault(d => d.IpAddress == ipAddress) ?? devices.FirstOrDefault();
    }

    /// <summary>Test hook: parses one captured/constructed mDNS reply and builds the devices it describes.</summary>
    public IReadOnlyList<DiscoveredDevice> ParseReply(byte[] packet)
    {
        var parser = new PacketParser(Services);
        parser.Parse(packet, IPAddress.Any);
        return Build(parser, 0);
    }

    private async Task<IReadOnlyList<DiscoveredDevice>> BrowseAsync(CancellationToken ct)
    {
        var parser = new PacketParser(Services);

        try
        {
            using var socket = CreateSocket();

            var multicast = IPAddress.Parse(MulticastAddress);
            var queries = MdnsPacket.BuildPtrQuery(Services, unicastResponse: socket.LocalEndPoint is { } local &&
                                                                     ((IPEndPoint)local).Port != MdnsPort);

            foreach (var interfaceAddress in LocalAddresses())
            {
                try
                {
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface,
                        interfaceAddress.GetAddressBytes());
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);

                    await socket.SendToAsync(queries, SocketFlags.None,
                        new IPEndPoint(multicast, MdnsPort), ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "{Protocol}: mDNS query via {Address} failed", ProtocolName, interfaceAddress);
                }
            }

            await CollectAsync(socket, parser, TimeSpan.FromMilliseconds(2500), ct).ConfigureAwait(false);

            // second round: ask for the addresses of hosts we saw in SRV records but never got an A for
            var missing = parser.HostsWithoutAddress();
            if (missing.Count > 0)
            {
                var addressQueries = MdnsPacket.BuildAddressQuery(missing);
                await socket.SendToAsync(addressQueries, SocketFlags.None,
                    new IPEndPoint(multicast, MdnsPort), ct).ConfigureAwait(false);

                await CollectAsync(socket, parser, TimeSpan.FromMilliseconds(1500), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "{Protocol}: mDNS browse failed", ProtocolName);
        }

        return Build(parser, 0);
    }

    private Socket CreateSocket()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

        try
        {
            // 5353 is shared: ReuseAddress lets us join even when the system responder is running
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Bind(new IPEndPoint(IPAddress.Any, MdnsPort));

            foreach (var address in LocalAddresses())
            {
                try
                {
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
                        new MulticastOption(IPAddress.Parse(MulticastAddress), address));
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "{Protocol}: joining the mDNS group on {Address} failed", ProtocolName, address);
                }
            }
        }
        catch (SocketException ex)
        {
            // something else owns 5353 exclusively: fall back to an ephemeral port with the unicast-response bit
            _logger.LogDebug(ex, "{Protocol}: cannot bind 5353, using a unicast mDNS query", ProtocolName);
            socket.Dispose();

            socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(IPAddress.Any, 0));
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        }

        return socket;
    }

    private async Task CollectAsync(Socket socket, PacketParser parser, TimeSpan window, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + window;
        var buffer = new byte[9000];

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

                parser.Parse(new ArraySegment<byte>(buffer, 0, result.ReceivedBytes).ToArray(),
                    ((IPEndPoint)result.RemoteEndPoint).Address);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // keep waiting until the window closes
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.MessageSize)
            {
            }
        }
    }

    private IReadOnlyList<DiscoveredDevice> Build(PacketParser parser, int _)
    {
        var devices = new Dictionary<string, DiscoveredDevice>(StringComparer.OrdinalIgnoreCase);

        foreach (var instance in parser.Instances())
        {
            try
            {
                var device = ToDevice(instance);
                if (device == null) continue;

                if (devices.TryGetValue(device.IpAddress, out var known))
                {
                    // the same camera answers on more than one service; keep the richer record
                    if ((device.Properties?.Count ?? 0) > (known.Properties?.Count ?? 0))
                        devices[device.IpAddress] = Merge(known, device);
                }
                else
                {
                    devices[device.IpAddress] = device;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "{Protocol}: building a device from {Instance} failed", ProtocolName, instance);
            }
        }

        return devices.Values.ToList();
    }

    private static DiscoveredDevice Merge(DiscoveredDevice older, DiscoveredDevice newer) => newer with
    {
        MacAddress = newer.MacAddress ?? older.MacAddress,
        SerialNumber = newer.SerialNumber ?? older.SerialNumber,
        FirmwareVersion = newer.FirmwareVersion ?? older.FirmwareVersion,
        WebInterfaceUrl = newer.WebInterfaceUrl ?? older.WebInterfaceUrl
    };

    protected DiscoveredDevice CreateDevice(MdnsServiceInstance instance, string model, string serial, string mac,
        string? firmware, DeviceType type, Dictionary<string, object>? properties = null)
    {
        var address = instance.Address?.ToString() ?? "";

        return new DiscoveredDevice(
            IpAddress: address,
            Port: instance.Port > 0 ? instance.Port : null,
            Manufacturer: VendorName,
            Model: model,
            MacAddress: string.IsNullOrWhiteSpace(mac) ? null : mac,
            FirmwareVersion: string.IsNullOrWhiteSpace(firmware) ? null : firmware,
            HardwareVersion: null,
            SerialNumber: string.IsNullOrWhiteSpace(serial) ? null : serial,
            Type: type,
            Properties: properties,
            WebInterfaceUrl: null,
            ApiEndpoint: null,
            DiscoveryProtocol: ProtocolName);
    }

    private static IEnumerable<IPAddress> LocalAddresses()
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
            // fall through to the default interface
        }

        if (addresses.Count == 0) addresses.Add(IPAddress.Any);
        return addresses;
    }

    /// <summary>Joins PTR/SRV/TXT/A records into service instances.</summary>
    public sealed class PacketParser
    {
        private readonly HashSet<string> _services;
        private readonly Dictionary<string, MdnsServiceInstance> _instances = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, IPAddress> _addresses = new(StringComparer.OrdinalIgnoreCase);

        public PacketParser(IEnumerable<string> services)
            => _services = new HashSet<string>(services, StringComparer.OrdinalIgnoreCase);

        public IReadOnlyCollection<MdnsServiceInstance> Instances()
        {
            foreach (var instance in _instances.Values)
            {
                if (instance.Address == null && instance.Host.Length > 0 &&
                    _addresses.TryGetValue(instance.Host, out var address))
                    instance.Address = address;
            }

            // only instances of the services this protocol asked for: in a shared packet the PTR records
            // of every other service are present too, and their SRV/TXT/A tails must not become devices
            return _instances.Values
                .Where(i => i.Address != null && i.Service.Length > 0 && _services.Contains(i.Service))
                .ToList();
        }

        public IReadOnlyList<string> HostsWithoutAddress()
            => _instances.Values
                .Where(i => i.Address == null && i.Host.Length > 0)
                .Select(i => i.Host)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        public void Parse(byte[] packet, IPAddress from)
        {
            var message = MdnsPacket.Parse(packet);

            foreach (var record in message.Answers.Concat(message.Additional))
            {
                switch (record.Type)
                {
                    case MdnsPacket.TypePtr when record.TargetName != null:
                        if (!_services.Contains(record.Name)) break;
                        Ensure(record.TargetName).Service = record.Name;
                        break;

                    case MdnsPacket.TypeSrv:
                    {
                        var instance = Ensure(record.Name);
                        instance.Host = record.TargetName ?? "";
                        instance.Port = record.Port;
                        if (instance.Service.Length == 0) instance.Service = ServiceOf(record.Name);
                        break;
                    }

                    case MdnsPacket.TypeTxt:
                    {
                        var instance = Ensure(record.Name);
                        foreach (var entry in record.TextEntries)
                        {
                            var equals = entry.IndexOf('=');
                            if (equals <= 0)
                            {
                                if (entry.Length > 0) instance.Txt[entry] = "";
                                continue;
                            }

                            instance.Txt[entry[..equals].Trim()] = entry[(equals + 1)..];
                        }

                        break;
                    }

                    case MdnsPacket.TypeA or MdnsPacket.TypeAaaa when record.Address != null:
                        _addresses[record.Name] = record.Address;
                        break;
                }
            }

            // a device that answers from a host we already know keeps working without SRV parsing
            foreach (var instance in _instances.Values)
            {
                if (instance.Address == null && instance.Host.Length > 0 &&
                    _addresses.TryGetValue(instance.Host, out var address))
                    instance.Address = address;
            }

            _ = from;
        }

        private MdnsServiceInstance Ensure(string name)
        {
            if (!_instances.TryGetValue(name, out var instance))
                _instances[name] = instance = new MdnsServiceInstance { InstanceName = name };

            return instance;
        }

        private string ServiceOf(string name)
        {
            foreach (var service in _services)
            {
                if (name.EndsWith(service, StringComparison.OrdinalIgnoreCase)) return service;
            }

            return "";
        }
    }
}