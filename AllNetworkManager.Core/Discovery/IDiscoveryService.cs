namespace AllNetworkManager.Core.Discovery;

using AllNetworkManager.Core.Devices;
using System.Collections.Generic;
using System.Threading;

public interface IDiscoveryService
{
    string Name { get; }
    string[] SupportedProtocols { get; }
    Task<IEnumerable<DiscoveredDevice>> DiscoverAsync(CancellationToken ct = default);
    Task<IEnumerable<DiscoveredDevice>> DiscoverAsync(string subnet, CancellationToken ct = default);

    /// <summary>
    /// Scans the given targets — each one a CIDR subnet, an address range (192.168.0.10-192.168.0.60)
    /// or a single address. With <paramref name="activeScan"/> every address is also probed on the
    /// common service ports, which finds devices that do not answer multicast discovery.
    /// </summary>
    Task<IEnumerable<DiscoveredDevice>> DiscoverAsync(
        IReadOnlyList<string> targets, bool activeScan, CancellationToken ct = default);
    Task<DiscoveredDevice?> DiscoverSingleAsync(string ipAddress, CancellationToken ct = default);
    event EventHandler<DiscoveredDevice> DeviceDiscovered;
    event EventHandler<DiscoveryProgress> ProgressChanged;
}

public record DiscoveredDevice(
    string IpAddress,
    int? Port,
    string Manufacturer,
    string Model,
    string? MacAddress = null,
    string? FirmwareVersion = null,
    string? HardwareVersion = null,
    string? SerialNumber = null,
    DeviceType Type = DeviceType.Unknown,
    Dictionary<string, object>? Properties = null,
    Uri? WebInterfaceUrl = null,
    Uri? ApiEndpoint = null,
    string DiscoveryProtocol = ""
);

public record DiscoveryProgress(
    string CurrentSubnet,
    int DevicesFound,
    int SubnetsScanned,
    int TotalSubnets,
    string CurrentOperation
);

public interface IDiscoveryProtocol
{
    string ProtocolName { get; }
    Task<IEnumerable<DiscoveredDevice>> ScanAsync(string subnet, CancellationToken ct = default);
    Task<DiscoveredDevice?> ProbeAsync(string ipAddress, int port, CancellationToken ct = default);
    bool SupportsPort(int port);
}