namespace AllNetworkManager.Discovery.Protocols;

using AllNetworkManager.Core.Discovery;
using AllNetworkManager.Core.Devices;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Sockets;
using System.Text;

public class SsdpProtocol : IDiscoveryProtocol
{
    private readonly ILogger<SsdpProtocol> _logger;
    private const string MulticastAddress = "239.255.255.250";
    private const int MulticastPort = 1900;

    public string ProtocolName => "SSDP/UPnP";

    public SsdpProtocol(ILogger<SsdpProtocol> logger)
    {
        _logger = logger;
    }

    public bool SupportsPort(int port) => port == 1900 || port == 80 || port == 443 || port == 8080;

    public async Task<IEnumerable<DiscoveredDevice>> ScanAsync(string subnet, CancellationToken ct = default)
    {
        var devices = new List<DiscoveredDevice>();

        try
        {
            using var udpClient = new UdpClient();
            udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            
            var localEp = new IPEndPoint(IPAddress.Any, 0);
            udpClient.Client.Bind(localEp);

            var msearch = CreateMsearch();
            var multicastEp = new IPEndPoint(IPAddress.Parse(MulticastAddress), MulticastPort);
            var msearchBytes = Encoding.ASCII.GetBytes(msearch);

            await udpClient.SendAsync(msearchBytes, msearchBytes.Length, multicastEp);

            var endTime = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < endTime && !ct.IsCancellationRequested)
            {
                try
                {
                    if (udpClient.Available > 0)
                    {
                        var result = await udpClient.ReceiveAsync();
                        var response = Encoding.ASCII.GetString(result.Buffer);
                        var device = ParseSsdpResponse(response, result.RemoteEndPoint.Address.ToString());
                        if (device != null)
                            devices.Add(device);
                    }
                    await Task.Delay(100, ct);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Error receiving SSDP response");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SSDP scan failed");
        }

        return devices;
    }

    public async Task<DiscoveredDevice?> ProbeAsync(string ipAddress, int port, CancellationToken ct = default)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var url = $"http://{ipAddress}:{port}/description.xml";
            var response = await client.GetAsync(url, ct);
            if (response.IsSuccessStatusCode)
            {
                var xml = await response.Content.ReadAsStringAsync(ct);
                return ParseDeviceDescription(xml, ipAddress, port);
            }
        }
        catch { }
        return null;
    }

    private string CreateMsearch()
    {
        return "M-SEARCH * HTTP/1.1\r\n" +
               $"HOST: {MulticastAddress}:{MulticastPort}\r\n" +
               "MAN: \"ssdp:discover\"\r\n" +
               "MX: 3\r\n" +
               "ST: urn:schemas-upnp-org:device:basic:1\r\n" +
               "\r\n";
    }

    private DiscoveredDevice? ParseSsdpResponse(string response, string sourceIp)
    {
        try
        {
            var lines = response.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            
            foreach (var line in lines)
            {
                var colon = line.IndexOf(':');
                if (colon > 0)
                {
                    var key = line[..colon].Trim();
                    var value = line[(colon + 1)..].Trim();
                    headers[key] = value;
                }
            }

            if (headers.TryGetValue("LOCATION", out var location) && Uri.TryCreate(location, UriKind.Absolute, out var uri))
            {
                return new DiscoveredDevice(
                    IpAddress: sourceIp,
                    Port: uri.Port,
                    Manufacturer: headers.GetValueOrDefault("SERVER", "Unknown").Split('/')[0],
                    Model: "UPnP Device",
                    Properties: headers.ToDictionary(k => k.Key, v => (object)v.Value),
                    WebInterfaceUrl: new Uri($"http://{sourceIp}:{uri.Port}"),
                    ApiEndpoint: uri,
                    DiscoveryProtocol: "SSDP"
                );
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to parse SSDP response");
        }
        return null;
    }

    private DiscoveredDevice? ParseDeviceDescription(string xml, string ip, int port)
    {
        try
        {
            var doc = System.Xml.Linq.XDocument.Parse(xml);
            var ns = System.Xml.Linq.XNamespace.Get("urn:schemas-upnp-org:device-1-0");
            
            var device = doc.Descendants(ns + "device").FirstOrDefault();
            if (device == null) return null;

            var manufacturer = device.Element(ns + "manufacturer")?.Value ?? "Unknown";
            var model = device.Element(ns + "modelName")?.Value ?? "Unknown";
            var modelNumber = device.Element(ns + "modelNumber")?.Value ?? "";
            var serial = device.Element(ns + "serialNumber")?.Value ?? "";
            var firmware = device.Element(ns + "firmwareVersion")?.Value ?? "";
            var presentationUrl = device.Element(ns + "presentationURL")?.Value ?? "";

            return new DiscoveredDevice(
                IpAddress: ip,
                Port: port,
                Manufacturer: manufacturer,
                Model: $"{model} {modelNumber}".Trim(),
                SerialNumber: serial,
                FirmwareVersion: firmware,
                Properties: new Dictionary<string, object>
                {
                    ["presentationUrl"] = presentationUrl,
                    ["modelNumber"] = modelNumber
                },
                WebInterfaceUrl: string.IsNullOrEmpty(presentationUrl) ? new Uri($"http://{ip}:{port}") : new Uri(presentationUrl),
                ApiEndpoint: new Uri($"http://{ip}:{port}/description.xml"),
                DiscoveryProtocol: "SSDP"
            );
        }
        catch { }
        return null;
    }
}