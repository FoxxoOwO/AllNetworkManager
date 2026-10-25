namespace AllNetworkManager.Vendors.Microsens;

using System.Net;
using System.Net.Sockets;
using AllNetworkManager.Core.Discovery;
using AllNetworkManager.Core.Devices;
using Microsoft.Extensions.Logging;

/// <summary>
/// Passive listener for MICROSENS switch announcements (UDP 8340, plus the BOOTP-style request on UDP 67).
/// The switches broadcast on their own; the vendor tool does not send a discovery query either, so scanning
/// means "listen for a while". Port 67 needs administrator rights and is skipped silently if it cannot bind.
/// </summary>
public class MicrosensDiscoveryProtocol : IDiscoveryProtocol
{
    private readonly ILogger<MicrosensDiscoveryProtocol> _logger;
    private static readonly TimeSpan ScanWindow = TimeSpan.FromSeconds(7);

    public string ProtocolName => "MICROSENS switch IP protocol (UDP 8340/67)";

    public MicrosensDiscoveryProtocol(ILogger<MicrosensDiscoveryProtocol> logger) => _logger = logger;

    public bool SupportsPort(int port)
        => port is MicrosensFrame.DiscoveryPort or MicrosensFrame.SectionedPort or MicrosensFrame.LegacySetPort or 443;

    public async Task<IEnumerable<DiscoveredDevice>> ScanAsync(string subnet, CancellationToken ct = default)
    {
        var found = new Dictionary<string, DiscoveredDevice>(StringComparer.OrdinalIgnoreCase);
        var sockets = new List<UdpClient>();

        foreach (var port in new[] { MicrosensFrame.DiscoveryPort, MicrosensFrame.BootpPort })
        {
            try
            {
                var udp = new UdpClient();
                udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                udp.Client.Bind(new IPEndPoint(IPAddress.Any, port));
                sockets.Add(udp);
            }
            catch (Exception ex)
            {
                _logger.LogDebug("MICROSENS listener on UDP {Port} unavailable: {Message}", port, ex.Message);
            }
        }

        if (sockets.Count == 0) return found.Values;

        var deadline = DateTime.UtcNow + ScanWindow;

        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            foreach (var udp in sockets.ToList())
            {
                if (udp.Available == 0) continue;

                try
                {
                    var result = await udp.ReceiveAsync(ct).ConfigureAwait(false);
                    var device = Map(result.Buffer, result.RemoteEndPoint.Address.ToString());
                    if (device == null) continue;

                    if (found.TryGetValue(device.MacAddress ?? device.IpAddress, out var existing))
                        found[device.MacAddress ?? device.IpAddress] = Merge(existing, result.RemoteEndPoint.Address.ToString());
                    else
                        found[device.MacAddress ?? device.IpAddress] = device;
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _logger.LogDebug("MICROSENS receive error: {Message}", ex.Message);
                }
            }

            await Task.Delay(60, ct).ConfigureAwait(false);
        }

        foreach (var udp in sockets) udp.Dispose();
        _logger.LogInformation("MICROSENS scan found {Count} device(s)", found.Count);

        return found.Values;
    }

    public async Task<DiscoveredDevice?> ProbeAsync(string ipAddress, int port, CancellationToken ct = default)
    {
        try
        {
            using var udp = new UdpClient();
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, MicrosensFrame.DiscoveryPort));

            var deadline = DateTime.UtcNow.AddSeconds(4);
            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                if (udp.Available == 0)
                {
                    await Task.Delay(50, ct).ConfigureAwait(false);
                    continue;
                }

                var result = await udp.ReceiveAsync(ct).ConfigureAwait(false);
                if (!result.RemoteEndPoint.Address.Equals(IPAddress.Parse(ipAddress))) continue;

                return Map(result.Buffer, result.RemoteEndPoint.Address.ToString());
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug("MICROSENS probe {Ip} failed: {Message}", ipAddress, ex.Message);
        }

        return null;
    }

    private DiscoveredDevice? Map(byte[] buffer, string senderAddress)
    {
        if (!MicrosensFrame.TryParse(buffer, out var announce) || announce == null) return null;

        var ip = announce.IpAddress;
        if (ip is "0.0.0.0" or "255.255.255.255") ip = senderAddress;

        var properties = new Dictionary<string, object>
        {
            ["microsens.frame"] = buffer,
            ["microsens.version"] = announce.FrameVersion,
            ["microsens.sectioned"] = announce.Sectioned,
            ["microsens.mac"] = announce.MacAddress,
            ["microsens.dhcpMode"] = announce.DhcpMode,
            ["microsens.articleNumber"] = announce.ArticleNumber,
            ["microsens.hostname"] = announce.Hostname,
            ["microsens.location"] = announce.Location,
            ["microsens.group"] = announce.Group,
            ["microsens.subnetMask"] = announce.SubnetMask,
            ["microsens.gateway"] = announce.Gateway
        };

        Uri? web = announce.HttpsPort > 0
            ? new Uri($"https://{ip}:{announce.HttpsPort}")
            : new Uri($"https://{ip}");

        return new DiscoveredDevice(
            IpAddress: ip,
            Port: announce.HttpsPort > 0 ? announce.HttpsPort : 443,
            Manufacturer: "MICROSENS",
            Model: string.IsNullOrWhiteSpace(announce.ArticleNumber) ? "MS switch" : announce.ArticleNumber,
            MacAddress: announce.MacAddress,
            FirmwareVersion: announce.FirmwareVersion,
            HardwareVersion: announce.HardwareVersion,
            SerialNumber: announce.SerialNumber,
            Type: DeviceType.Switch,
            Properties: properties,
            WebInterfaceUrl: web,
            ApiEndpoint: null,
            DiscoveryProtocol: "MICROSENS UDP");
    }

    private static DiscoveredDevice Merge(DiscoveredDevice existing, string senderAddress) => existing;
}
