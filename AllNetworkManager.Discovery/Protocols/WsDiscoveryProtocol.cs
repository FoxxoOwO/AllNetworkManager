namespace AllNetworkManager.Discovery.Protocols;

using AllNetworkManager.Core.Discovery;
using AllNetworkManager.Core.Devices;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;

public class WsDiscoveryProtocol : IDiscoveryProtocol
{
    private readonly ILogger<WsDiscoveryProtocol> _logger;
    private const string MulticastAddress = "239.255.255.250";
    private const int MulticastPort = 3702;
    private const string Namespace = "http://schemas.xmlsoap.org/ws/2005/04/discovery";
    private const string DnNamespace = "http://www.onvif.org/ver10/network/wsdl";

    public string ProtocolName => "WS-Discovery";

    public WsDiscoveryProtocol(ILogger<WsDiscoveryProtocol> logger)
    {
        _logger = logger;
    }

    public bool SupportsPort(int port) => port == 3702 || port == 80 || port == 443;

    public async Task<IEnumerable<DiscoveredDevice>> ScanAsync(string subnet, CancellationToken ct = default)
    {
        var devices = new List<DiscoveredDevice>();
        
        try
        {
            using var udpClient = new UdpClient();
            udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            
            var localEp = new IPEndPoint(IPAddress.Any, 0);
            udpClient.Client.Bind(localEp);

            var probeMessage = CreateProbeMessage();
            var multicastEp = new IPEndPoint(IPAddress.Parse(MulticastAddress), MulticastPort);
            var probeBytes = Encoding.UTF8.GetBytes(probeMessage);

            await udpClient.SendAsync(probeBytes, probeBytes.Length, multicastEp);

            var receiveTask = Task.Run(async () =>
            {
                var endTime = DateTime.UtcNow.AddSeconds(5);
                while (DateTime.UtcNow < endTime && !ct.IsCancellationRequested)
                {
                    try
                    {
                        if (udpClient.Available > 0)
                        {
                            var result = await udpClient.ReceiveAsync();
                            var response = Encoding.UTF8.GetString(result.Buffer);
                            var device = ParseProbeMatch(response, result.RemoteEndPoint.Address.ToString());
                            if (device != null)
                                devices.Add(device);
                        }
                        await Task.Delay(100, ct);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Error receiving WS-Discovery response");
                    }
                }
            }, ct);

            await Task.WhenAny(receiveTask, Task.Delay(6000, ct));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WS-Discovery scan failed");
        }

        return devices;
    }

    public async Task<DiscoveredDevice?> ProbeAsync(string ipAddress, int port, CancellationToken ct = default)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var urls = new[]
            {
                $"http://{ipAddress}:{port}/onvif/device_service",
                $"http://{ipAddress}:{port}/onvif/DeviceService",
                $"http://{ipAddress}:{port}/onvif/services",
                $"https://{ipAddress}:{port}/onvif/device_service"
            };

            foreach (var url in urls)
            {
                try
                {
                    var probeMsg = CreateProbeMessage();
                    var content = new StringContent(probeMsg, Encoding.UTF8, "application/soap+xml");
                    content.Headers.Add("SOAPAction", "\"http://schemas.xmlsoap.org/ws/2005/04/discovery/Probe\"");
                    
                    var response = await client.PostAsync(url, content, ct);
                    if (response.IsSuccessStatusCode)
                    {
                        var responseText = await response.Content.ReadAsStringAsync(ct);
                        return ParseProbeMatch(responseText, ipAddress);
                    }
                }
                catch { continue; }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "WS-Discovery probe failed for {Ip}", ipAddress);
        }
        return null;
    }

    private string CreateProbeMessage()
    {
        var messageId = Guid.NewGuid().ToString();
        return $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<e:Envelope xmlns:e=""http://www.w3.org/2003/05/soap-envelope""
            xmlns:w=""http://schemas.xmlsoap.org/ws/2004/08/addressing""
            xmlns:d=""http://schemas.xmlsoap.org/ws/2005/04/discovery""
            xmlns:dn=""http://www.onvif.org/ver10/network/wsdl"">
    <e:Header>
        <w:MessageID>uuid:{messageId}</w:MessageID>
        <w:To e:mustUnderstand=""true"">urn:schemas-xmlsoap-org:ws:2005:04:discovery</w:To>
        <w:Action e:mustUnderstand=""true"">http://schemas.xmlsoap.org/ws/2005/04/discovery/Probe</w:Action>
    </e:Header>
    <e:Body>
        <d:Probe>
            <d:Types>dn:NetworkVideoTransmitter</d:Types>
        </d:Probe>
    </e:Body>
</e:Envelope>";
    }

    private DiscoveredDevice? ParseProbeMatch(string xml, string sourceIp)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            var ns = XNamespace.Get("http://schemas.xmlsoap.org/ws/2005/04/discovery");
            var dn = XNamespace.Get("http://www.onvif.org/ver10/network/wsdl");
            var wsa = XNamespace.Get("http://schemas.xmlsoap.org/ws/2004/08/addressing");

            var probeMatches = doc.Descendants(ns + "ProbeMatch");
            foreach (var match in probeMatches)
            {
                var xaddrs = match.Element(ns + "XAddrs")?.Value.Split(' ') ?? [];
                var endpoint = xaddrs.FirstOrDefault(x => x.StartsWith("http")) ?? $"http://{sourceIp}";
                var uri = new Uri(endpoint);
                
                var scopes = match.Element(ns + "Scopes")?.Value.Split(' ') ?? [];
                var metadata = ParseScopes(scopes);

                return new DiscoveredDevice(
                    IpAddress: sourceIp,
                    Port: uri.Port,
                    Manufacturer: metadata.GetValueOrDefault("manufacturer") as string ?? "Unknown",
                    Model: metadata.GetValueOrDefault("model") as string ?? "Unknown",
                    MacAddress: metadata.GetValueOrDefault("mac") as string,
                    FirmwareVersion: metadata.GetValueOrDefault("firmware") as string,
                    HardwareVersion: metadata.GetValueOrDefault("hardware") as string,
                    SerialNumber: metadata.GetValueOrDefault("serial") as string,
                    Type: DeviceType.Camera,
                    Properties: metadata,
                    WebInterfaceUrl: new Uri($"http://{sourceIp}:{uri.Port}"),
                    ApiEndpoint: new Uri(endpoint),
                    DiscoveryProtocol: "WS-Discovery"
                );
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to parse WS-Discovery ProbeMatch");
        }
        return null;
    }

    private Dictionary<string, object> ParseScopes(string[] scopes)
    {
        var dict = new Dictionary<string, object>();
        foreach (var scope in scopes)
        {
            if (scope.StartsWith("onvif://www.onvif.org/"))
            {
                var parts = scope.Split('/');
                if (parts.Length >= 4)
                {
                    var key = parts[3].ToLowerInvariant();
                    var value = parts.Length > 4 ? string.Join("/", parts[4..]) : "";
                    dict[key] = value;
                }
            }
        }
        return dict;
    }
}