namespace AllNetworkManager.Vendors.Onvif;

using AllNetworkManager.Core.Onvif;
using AllNetworkManager.Core.Exceptions;
using AllNetworkManager.Core.Devices;
using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using System.Xml.Linq;

public class OnvifClient : IOnvifClient
{
    private readonly ILogger<OnvifClient> _logger;
    private readonly HttpClient _httpClient;
    private readonly string _deviceAddress;
    private readonly DeviceCredentials? _credentials;

    public string DeviceAddress => _deviceAddress;

    public OnvifClient(ILogger<OnvifClient> logger, HttpClient httpClient, string deviceAddress, DeviceCredentials? credentials = null)
    {
        _logger = logger;
        _httpClient = httpClient;
        _deviceAddress = deviceAddress;
        _credentials = credentials;
    }

    public async Task<OnvifDeviceInfo> GetDeviceInfoAsync(CancellationToken ct = default)
    {
        var response = await SendSoapAsync("GetDeviceInformation", """
            <tds:GetDeviceInformation xmlns:tds="http://www.onvif.org/ver10/device/wsdl"/>
            """, ct);

        var manufacturer = GetElementValue(response, "Manufacturer");
        var model = GetElementValue(response, "Model");
        var firmware = GetElementValue(response, "FirmwareVersion");
        var serial = GetElementValue(response, "SerialNumber");
        var hardware = GetElementValue(response, "HardwareId");

        return new OnvifDeviceInfo(manufacturer, model, firmware, serial, hardware, null);
    }

    public async Task<OnvifNetworkInterfaces> GetNetworkInterfacesAsync(CancellationToken ct = default)
    {
        var response = await SendSoapAsync("GetNetworkInterfaces", """
            <tds:GetNetworkInterfaces xmlns:tds="http://www.onvif.org/ver10/device/wsdl"/>
            """, ct);

        var interfaces = new List<OnvifNetworkInterface>();
        var ns = XNamespace.Get("http://www.onvif.org/ver10/device/wsdl");
        
        foreach (var iface in response.Descendants(ns + "NetworkInterfaces"))
        {
            var token = iface.Element(ns + "token")?.Value ?? "";
            var name = iface.Element(ns + "Name")?.Value ?? "";
            var enabled = bool.Parse(iface.Element(ns + "Enabled")?.Value ?? "true");

            OnvifIpv4Config? ipv4 = null;
            var ipv4Elem = iface.Element(ns + "IPv4");
            if (ipv4Elem != null)
            {
                ipv4 = new OnvifIpv4Config(
                    Enabled: bool.Parse(ipv4Elem.Element(ns + "Enabled")?.Value ?? "true"),
                    Mode: Enum.Parse<OnvifIpConfigMode>(ipv4Elem.Element(ns + "Config")?.Element(ns + "Type")?.Value ?? "DHCP"),
                    Address: ipv4Elem.Element(ns + "Config")?.Element(ns + "Address")?.Value,
                    PrefixLength: ipv4Elem.Element(ns + "Config")?.Element(ns + "PrefixLength")?.Value,
                    Gateway: ipv4Elem.Element(ns + "Config")?.Element(ns + "Gateway")?.Value,
                    DnsServers: ipv4Elem.Element(ns + "Config")?.Elements(ns + "DNS")?.Select(d => d.Value).ToArray()
                );
            }

            interfaces.Add(new OnvifNetworkInterface(token, name, enabled, ipv4, null, null));
        }

        return new OnvifNetworkInterfaces(interfaces);
    }

    public async Task<bool> SetNetworkInterfacesAsync(OnvifNetworkInterfaces config, CancellationToken ct = default)
    {
        var ifaceXml = string.Join("", config.Interfaces.Select(i => $"""
            <tds:NetworkInterface>
                <tds:token>{i.Token}</tds:token>
                <tds:Enabled>{i.Enabled.ToString().ToLower()}</tds:Enabled>
                {GetIpv4Xml(i.Ipv4)}
            </tds:NetworkInterface>
            """));

        var response = await SendSoapAsync("SetNetworkInterfaces", $"""
            <tds:SetNetworkInterfaces xmlns:tds="http://www.onvif.org/ver10/device/wsdl">
                {ifaceXml}
            </tds:SetNetworkInterfaces>
            """, ct);

        return true;
    }

    private string GetIpv4Xml(OnvifIpv4Config? ipv4)
    {
        if (ipv4 == null) return "";
        var dns = ipv4.DnsServers != null ? string.Join("", ipv4.DnsServers.Select(d => $"<tds:DNS>{d}</tds:DNS>")) : "";
        return $"""
            <tds:IPv4>
                <tds:Enabled>{ipv4.Enabled.ToString().ToLower()}</tds:Enabled>
                <tds:Config>
                    <tds:Type>{ipv4.Mode}</tds:Type>
                    {(ipv4.Address != null ? $"<tds:Address>{ipv4.Address}</tds:Address>" : "")}
                    {(ipv4.PrefixLength != null ? $"<tds:PrefixLength>{ipv4.PrefixLength}</tds:PrefixLength>" : "")}
                    {(ipv4.Gateway != null ? $"<tds:Gateway>{ipv4.Gateway}</tds:Gateway>" : "")}
                    {dns}
                </tds:Config>
            </tds:IPv4>
            """;
    }

    public async Task<SystemDateTime> GetSystemDateTimeAsync(CancellationToken ct = default)
    {
        var response = await SendSoapAsync("GetSystemDateAndTime", """
            <tds:GetSystemDateAndTime xmlns:tds="http://www.onvif.org/ver10/device/wsdl"/>
            """, ct);

        var utcTimeStr = GetElementValue(response, "UTCDateTime");
        var timeStr = GetElementValue(response, "Time");
        var dateStr = GetElementValue(response, "Date");
        
        var utcTime = DateTime.TryParse(utcTimeStr, out var dt) ? dt : DateTime.UtcNow;
        var tz = GetElementValue(response, "TimeZone");
        var dst = bool.TryParse(GetElementValue(response, "DaylightSavings"), out var d) && d;

        return new SystemDateTime(utcTime, dst, tz);
    }

    public async Task<bool> SetSystemDateTimeAsync(SystemDateTime dateTime, CancellationToken ct = default)
    {
        var response = await SendSoapAsync("SetSystemDateAndTime", $"""
            <tds:SetSystemDateAndTime xmlns:tds="http://www.onvif.org/ver10/device/wsdl">
                <tds:DateTimeType>Manual</tds:DateTimeType>
                <tds:DaylightSavings>{dateTime.DaylightSavings.ToString().ToLower()}</tds:DaylightSavings>
                <tds:TimeZone>{dateTime.TimeZone}</tds:TimeZone>
                <tds:UTCDateTime>
                    <tds:Time>
                        <tds:Hour>{dateTime.UtcTime.Hour}</tds:Hour>
                        <tds:Minute>{dateTime.UtcTime.Minute}</tds:Minute>
                        <tds:Second>{dateTime.UtcTime.Second}</tds:Second>
                    </tds:Time>
                    <tds:Date>
                        <tds:Year>{dateTime.UtcTime.Year}</tds:Year>
                        <tds:Month>{dateTime.UtcTime.Month}</tds:Month>
                        <tds:Day>{dateTime.UtcTime.Day}</tds:Day>
                    </tds:Date>
                </tds:UTCDateTime>
            </tds:SetSystemDateAndTime>
            """, ct);

        return true;
    }

    public async Task<OnvifUsers> GetUsersAsync(CancellationToken ct = default)
    {
        var response = await SendSoapAsync("GetUsers", """
            <tds:GetUsers xmlns:tds="http://www.onvif.org/ver10/device/wsdl"/>
            """, ct);

        var users = new List<OnvifUser>();
        var ns = XNamespace.Get("http://www.onvif.org/ver10/device/wsdl");

        foreach (var user in response.Descendants(ns + "User"))
        {
            var username = user.Element(ns + "Username")?.Value ?? "";
            var level = Enum.TryParse<OnvifUserLevel>(user.Element(ns + "UserLevel")?.Value, out var l) ? l : OnvifUserLevel.MediaUser;
            users.Add(new OnvifUser(username, level, null));
        }

        return new OnvifUsers(users);
    }

    public async Task<bool> CreateUserAsync(OnvifUser user, CancellationToken ct = default)
    {
        var response = await SendSoapAsync("CreateUsers", $"""
            <tds:CreateUsers xmlns:tds="http://www.onvif.org/ver10/device/wsdl">
                <tds:User>
                    <tds:Username>{user.Username}</tds:Username>
                    <tds:UserLevel>{user.Level}</tds:UserLevel>
                    {(user.Password != null ? $"<tds:Password>{user.Password}</tds:Password>" : "")}
                </tds:User>
            </tds:CreateUsers>
            """, ct);

        return true;
    }

    public async Task<bool> DeleteUserAsync(string username, CancellationToken ct = default)
    {
        var response = await SendSoapAsync("DeleteUsers", $"""
            <tds:DeleteUsers xmlns:tds="http://www.onvif.org/ver10/device/wsdl">
                <tds:Username>{username}</tds:Username>
            </tds:DeleteUsers>
            """, ct);

        return true;
    }

    public async Task<bool> SetUserPasswordAsync(string username, string password, CancellationToken ct = default)
    {
        var response = await SendSoapAsync("SetUser", $"""
            <tds:SetUser xmlns:tds="http://www.onvif.org/ver10/device/wsdl">
                <tds:User>
                    <tds:Username>{username}</tds:Username>
                    <tds:Password>{password}</tds:Password>
                </tds:User>
            </tds:SetUser>
            """, ct);

        return true;
    }

    public async Task<OnvifCapabilities> GetCapabilitiesAsync(CancellationToken ct = default)
    {
        var response = await SendSoapAsync("GetCapabilities", """
            <tds:GetCapabilities xmlns:tds="http://www.onvif.org/ver10/device/wsdl">
                <tds:Category>All</tds:Category>
            </tds:GetCapabilities>
            """, ct);

        var caps = response.Descendants(XNamespace.Get("http://www.onvif.org/ver10/device/wsdl") + "Capabilities").FirstOrDefault();
        
        return new OnvifCapabilities(
            Device: caps?.Element(XNamespace.Get("http://www.onvif.org/ver10/device/wsdl") + "Device") != null,
            Events: caps?.Element(XNamespace.Get("http://www.onvif.org/ver10/events/wsdl") + "Events") != null,
            Imaging: caps?.Element(XNamespace.Get("http://www.onvif.org/ver20/imaging/wsdl") + "Imaging") != null,
            Media: caps?.Element(XNamespace.Get("http://www.onvif.org/ver10/media/wsdl") + "Media") != null,
            Ptz: caps?.Element(XNamespace.Get("http://www.onvif.org/ver20/ptz/wsdl") + "PTZ") != null,
            Recording: caps?.Element(XNamespace.Get("http://www.onvif.org/ver10/recording/wsdl") + "Recording") != null,
            Search: caps?.Element(XNamespace.Get("http://www.onvif.org/ver10/search/wsdl") + "Search") != null,
            Replay: caps?.Element(XNamespace.Get("http://www.onvif.org/ver10/replay/wsdl") + "Replay") != null,
            Analytics: caps?.Element(XNamespace.Get("http://www.onvif.org/ver20/analytics/wsdl") + "Analytics") != null,
            DeviceIO: caps?.Element(XNamespace.Get("http://www.onvif.org/ver10/deviceio/wsdl") + "DeviceIO") != null
        );
    }

    public async Task RebootAsync(CancellationToken ct = default)
    {
        await SendSoapAsync("SystemReboot", """
            <tds:SystemReboot xmlns:tds="http://www.onvif.org/ver10/device/wsdl"/>
            """, ct);
    }

    public async Task FactoryResetAsync(CancellationToken ct = default)
    {
        await SendSoapAsync("SetSystemFactoryDefault", """
            <tds:SetSystemFactoryDefault xmlns:tds="http://www.onvif.org/ver10/device/wsdl">
                <tds:Type>Hard</tds:Type>
            </tds:SetSystemFactoryDefault>
            """, ct);
    }

    public async Task<Stream> GetSnapshotUriAsync(string profileToken, CancellationToken ct = default)
    {
        var response = await SendSoapAsync("GetSnapshotUri", $"""
            <trt:GetSnapshotUri xmlns:trt="http://www.onvif.org/ver10/media/wsdl">
                <trt:ProfileToken>{profileToken}</trt:ProfileToken>
            </trt:GetSnapshotUri>
            """, ct);

        var uri = GetElementValue(response, "Uri");
        var httpResponse = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        return await httpResponse.Content.ReadAsStreamAsync(ct);
    }

    public async Task<Uri> GetStreamUriAsync(string profileToken, StreamType streamType, CancellationToken ct = default)
    {
        var protocol = streamType switch
        {
            StreamType.RTP_Unicast => "RTSP",
            StreamType.RTP_Multicast => "RTSP",
            StreamType.RTP_RTSP_HTTP => "HTTP",
            StreamType.RTP_RTSP_TCP => "RTSP",
            _ => "RTSP"
        };

        var response = await SendSoapAsync("GetStreamUri", $"""
            <trt:GetStreamUri xmlns:trt="http://www.onvif.org/ver10/media/wsdl">
                <trt:ProfileToken>{profileToken}</trt:ProfileToken>
                <trt:StreamSetup>
                    <trt:Stream>RTP-Unicast</trt:Stream>
                    <trt:Transport>
                        <trt:Protocol>{protocol}</trt:Protocol>
                    </trt:Transport>
                </trt:StreamSetup>
            </trt:GetStreamUri>
            """, ct);

        var uri = GetElementValue(response, "Uri");
        return new Uri(uri);
    }

    public async Task<PtzStatus> GetPtzStatusAsync(string profileToken, CancellationToken ct = default)
    {
        var response = await SendSoapAsync("GetStatus", $"""
            <tptz:GetStatus xmlns:tptz="http://www.onvif.org/ver20/ptz/wsdl">
                <tptz:ProfileToken>{profileToken}</tptz:ProfileToken>
            </tptz:GetStatus>
            """, ct);

        var ns = XNamespace.Get("http://www.onvif.org/ver20/ptz/wsdl");
        var position = response.Descendants(ns + "Position").FirstOrDefault();
        
        var ptzPos = new PtzPosition(0, 0, 0);
        if (position != null)
        {
            var panTilt = position.Element(ns + "PanTilt");
            var zoom = position.Element(ns + "Zoom");
            
            if (panTilt != null)
            {
                ptzPos = new PtzPosition(
                    double.Parse(panTilt.Attribute("x")?.Value ?? "0"),
                    double.Parse(panTilt.Attribute("y")?.Value ?? "0"),
                    ptzPos.Zoom
                );
            }
            if (zoom != null)
            {
                ptzPos = new PtzPosition(ptzPos.Pan, ptzPos.Tilt, double.Parse(zoom.Attribute("x")?.Value ?? "0"));
            }
        }

        return new PtzStatus(profileToken, ptzPos, null, null, DateTime.UtcNow);
    }

    public async Task<bool> PtzMoveAsync(string profileToken, PtzVector vector, CancellationToken ct = default)
    {
        var panTilt = "";
        if (vector.PanTiltX.HasValue || vector.PanTiltY.HasValue)
        {
            panTilt = $"""
                <tptz:PanTilt x="{vector.PanTiltX ?? 0}" y="{vector.PanTiltY ?? 0}"/>
                """;
        }

        var zoom = "";
        if (vector.Zoom.HasValue)
        {
            zoom = $"""
                <tptz:Zoom x="{vector.Zoom.Value}"/>
                """;
        }

        var response = await SendSoapAsync("ContinuousMove", $"""
            <tptz:ContinuousMove xmlns:tptz="http://www.onvif.org/ver20/ptz/wsdl">
                <tptz:ProfileToken>{profileToken}</tptz:ProfileToken>
                <tptz:Velocity>
                    {panTilt}
                    {zoom}
                </tptz:Velocity>
            </tptz:ContinuousMove>
            """, ct);

        return true;
    }

    public async Task<bool> PtzStopAsync(string profileToken, CancellationToken ct = default)
    {
        var response = await SendSoapAsync("Stop", $"""
            <tptz:Stop xmlns:tptz="http://www.onvif.org/ver20/ptz/wsdl">
                <tptz:ProfileToken>{profileToken}</tptz:ProfileToken>
                <tptz:PanTilt>true</tptz:PanTilt>
                <tptz:Zoom>true</tptz:Zoom>
            </tptz:Stop>
            """, ct);

        return true;
    }

    public async Task<OnvifRecordingConfig> GetRecordingConfigAsync(CancellationToken ct = default)
    {
        var response = await SendSoapAsync("GetRecordingOptions", """
            <trc:GetRecordingOptions xmlns:trc="http://www.onvif.org/ver10/recording/wsdl"/>
            """, ct);

        return new OnvifRecordingConfig(false, Array.Empty<OnvifRecordingJob>());
    }

    public async Task<OnvifAnalyticsConfig> GetAnalyticsConfigAsync(CancellationToken ct = default)
    {
        var response = await SendSoapAsync("GetAnalyticsModules", """
            <tan:GetAnalyticsModules xmlns:tan="http://www.onvif.org/ver20/analytics/wsdl"/>
            """, ct);

        return new OnvifAnalyticsConfig(false, Array.Empty<OnvifAnalyticsEngine>());
    }

    private async Task<XDocument> SendSoapAsync(string action, string body, CancellationToken ct)
    {
        var soapEnvelope = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<s:Envelope xmlns:s=""http://www.w3.org/2003/05/soap-envelope""
            xmlns:tds=""http://www.onvif.org/ver10/device/wsdl"">
    <s:Header>
        <wsa:Action xmlns:wsa=""http://www.w3.org/2005/08/addressing"">http://www.onvif.org/ver10/device/wsdl/{action}</wsa:Action>
        <wsa:MessageID xmlns:wsa=""http://www.w3.org/2005/08/addressing"">urn:uuid:{Guid.NewGuid()}</wsa:MessageID>
    </s:Header>
    <s:Body>
        {body}
    </s:Body>
</s:Envelope>";

        var content = new StringContent(soapEnvelope, System.Text.Encoding.UTF8, "application/soap+xml");
        
        var request = new HttpRequestMessage(HttpMethod.Post, _deviceAddress)
        {
            Content = content
        };
        
        if (_credentials != null)
        {
            var token = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($"{_credentials.Username}:{_credentials.Password}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
        }

        var response = await _httpClient.SendAsync(request, ct);
        
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            throw new DeviceManagerException($"ONVIF {action} failed: {response.StatusCode}", 
                _deviceAddress, "Bosch", ErrorCode.ProtocolError);
        }

        var responseText = await response.Content.ReadAsStringAsync(ct);
        return XDocument.Parse(responseText);
    }

    private string GetElementValue(XDocument doc, string localName)
    {
        return doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == localName)?.Value ?? "";
    }
}