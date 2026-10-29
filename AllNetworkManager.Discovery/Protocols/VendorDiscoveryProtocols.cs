namespace AllNetworkManager.Discovery.Protocols;

using AllNetworkManager.Core.Devices;
using AllNetworkManager.Core.Discovery;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

/// <summary>Hikvision SADP: XML probe to 239.255.255.250:37020, XML reply with a ProbeMatch block.</summary>
public sealed class HikvisionDiscoveryProtocol : UdpDiscoveryProtocolBase
{
    private const int Port = 37020;

    public HikvisionDiscoveryProtocol(ILogger<HikvisionDiscoveryProtocol> logger) : base(logger) { }

    public override string ProtocolName => "Hikvision SADP";
    protected override string VendorName => "Hikvision";
    protected override int RequestPort => Port;
    protected override IPAddress[] RequestAddresses => new[] { IPAddress.Parse("239.255.255.250") };
    protected override TimeSpan ListenWindow => TimeSpan.FromSeconds(3);

    protected override byte[] BuildRequest() =>
        Encoding.UTF8.GetBytes(
            "<?xml version=\"1.0\" encoding=\"utf-8\"?><Probe><Uuid>3b1c1f5a-9b3e-4a7f-8f3c-2a5f6a1d0c11</Uuid><Types>inquiry</Types></Probe>");

    protected override void Parse(byte[] data, IPEndPoint from, List<DiscoveredDevice> found)
    {
        var xml = Encoding.UTF8.GetString(data).Trim('\0', '\r', '\n', ' ');
        if (!xml.Contains("<ProbeMatch>", StringComparison.Ordinal)) return;

        try
        {
            // the device sends a bare ProbeMatch element, wrap it so XDocument can parse it
            var document = XDocument.Parse(xml.TrimStart('\uFEFF'));
            XElement? match = document.Descendants("ProbeMatch").FirstOrDefault();
            if (match == null) return;

            string Value(string name) => match.Element(name)?.Value.Trim() ?? "";

            var ip = Value("IPv4Address");
            if (!IPAddress.TryParse(ip, out _)) ip = from.Address.ToString();

            var properties = new Dictionary<string, object>
            {
                ["description"] = Value("DeviceDescription"),
                ["deviceType"] = Value("DeviceType"),
                ["commandPort"] = Value("CommandPort"),
                ["httpPort"] = Value("HttpPort"),
                ["mask"] = Value("IPv4SubnetMask"),
                ["gateway"] = Value("IPv4Gateway"),
                ["dhcp"] = Value("DHCP"),
                ["ipv6"] = Value("IPv6Address"),
                ["protocol"] = Value("Types")
            };

            var mac = Value("MAC").Replace('-', ':').ToUpperInvariant();
            var httpPort = Value("HttpPort");
            var webUrl = httpPort.Length > 0 && httpPort != "80"
                ? new Uri($"http://{ip}:{httpPort}")
                : new Uri($"http://{ip}");

            var device = Device(ip, Value("DeviceDescription"), Value("DeviceSN"), mac, Value("DeviceType"), properties);
            found.Add(device with
            {
                MacAddress = mac.Length == 17 ? mac : device.MacAddress,
                WebInterfaceUrl = webUrl
            });
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Hikvision SADP: malformed reply from {From}", from);
        }
    }
}

/// <summary>Dahua DHIP discovery: JSON body behind a 32-byte DHIP header, multicast 239.255.255.251:37810.</summary>
public sealed class DahuaDhipDiscoveryProtocol : UdpDiscoveryProtocolBase
{
    private const int Port = 37810;
    private const uint Magic = 0x44484950; // "DHIP"
    private const int HeaderSize = 0x20;

    public DahuaDhipDiscoveryProtocol(ILogger<DahuaDhipDiscoveryProtocol> logger) : base(logger) { }

    public override string ProtocolName => "Dahua DHIP";
    protected override string VendorName => "Dahua";
    protected override int RequestPort => Port;
    protected override IPAddress[] RequestAddresses => new[] { IPAddress.Parse("239.255.255.251") };

    protected override byte[] BuildRequest()
    {
        var body = Encoding.UTF8.GetBytes("{\"method\":\"DHDiscover.search\",\"params\":{\"mac\":\"\",\"uni\":0}}\n");
        var packet = new byte[HeaderSize + body.Length];

        WriteUInt32(packet, 0x00, HeaderSize);      // header size, little endian
        packet[0x04] = unchecked((byte)(Magic >> 24));   // magic is big endian on the wire
        packet[0x05] = unchecked((byte)(Magic >> 16));
        packet[0x06] = unchecked((byte)(Magic >> 8));
        packet[0x07] = unchecked((byte)Magic);
        WriteUInt32(packet, 0x10, (uint)body.Length);
        WriteUInt32(packet, 0x18, (uint)body.Length);

        body.CopyTo(packet, HeaderSize);
        return packet;
    }

    protected override void Parse(byte[] data, IPEndPoint from, List<DiscoveredDevice> found)
    {
        if (data.Length <= HeaderSize) return;
        if (ReadUInt32(data, 0x00) != HeaderSize) return;

        var tag = (uint)(data[0x04] << 24 | data[0x05] << 16 | data[0x06] << 8 | data[0x07]);
        if (tag != Magic) return;

        var bodyLength = (int)Math.Min(ReadUInt32(data, 0x10), (uint)(data.Length - HeaderSize));
        if (bodyLength <= 0) return;

        var body = Encoding.UTF8.GetString(data, HeaderSize, bodyLength);

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            if (root.TryGetProperty("method", out var method) &&
                method.GetString() != "client.notifyDevInfo")
                return;

            var info = root.TryGetProperty("params", out var parameters) &&
                       parameters.TryGetProperty("deviceInfo", out var deviceInfo)
                ? deviceInfo
                : root;

            string Text(string name) =>
                info.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
                    ? element.GetString() ?? ""
                    : "";

            var ip = Text("IPv4Address");
            if (!IPAddress.TryParse(ip, out _) &&
                info.TryGetProperty("IPv4Address", out var ipv4) && ipv4.ValueKind == JsonValueKind.Object)
                ip = Text2(ipv4, "IPAddress");

            if (!IPAddress.TryParse(ip, out _)) ip = from.Address.ToString();

            var properties = new Dictionary<string, object>
            {
                ["version"] = Text("Version"),
                ["buildDate"] = Text("BuildDate"),
                ["mask"] = Text2(info, "IPv4Address", "SubnetMask"),
                ["gateway"] = Text2(info, "IPv4Address", "DefaultGateway"),
                ["dhcp"] = Text2(info, "IPv4Address", "DhcpEnable"),
                ["httpPort"] = Text("HttpPort"),
                ["videoInputs"] = Text("VideoInputChannels"),
                ["videoOutputs"] = Text("VideoOutputChannels"),
                ["alarmInputs"] = Text("AlarmInputChannels")
            };

            found.Add(Device(ip, Text("DeviceType"), Text("SerialNo"), Normalize(Text("mac")),
                Text("Version"), properties));
        }
        catch (JsonException ex)
        {
            // Some devices (and the vendor's own capture) emit a truncated string. Fall back to a tolerant
            // key/value scan instead of dropping a device that is otherwise perfectly discoverable.
            Logger.LogDebug(ex, "Dahua DHIP: JSON body from {From} is malformed, using tolerant field scan", from);

            var ip = TolerantField(body, "IPAddress");
            if (!IPAddress.TryParse(ip, out _)) ip = from.Address.ToString();

            var properties = new Dictionary<string, object>
            {
                ["malformedJson"] = "true",
                ["version"] = TolerantField(body, "Version"),
                ["vendor"] = TolerantField(body, "Vendor")
            };

            found.Add(Device(ip, TolerantField(body, "DeviceType"), TolerantField(body, "SerialNo"),
                Normalize(TolerantField(body, "mac")), TolerantField(body, "Version"), properties));
        }

        static string Normalize(string mac) => mac.Replace('-', ':').ToUpperInvariant();

        static string TolerantField(string json, string key)
        {
            var match = System.Text.RegularExpressions.Regex.Match(json,
                "\"" + System.Text.RegularExpressions.Regex.Escape(key) + "\"\\s*:\\s*\"([^\"\\r\\n,}]*)",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant);

            return match.Success ? match.Groups[1].Value.Trim() : "";
        }

        static string Text2(JsonElement parent, params string[] path)
        {
            var current = parent;
            foreach (var name in path)
            {
                if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out var next))
                    return "";
                current = next;
            }

            return current.ValueKind switch
            {
                JsonValueKind.String => current.GetString() ?? "",
                JsonValueKind.Number => current.ToString(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => ""
            };
        }
    }

    private static void WriteUInt32(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
        buffer[offset + 3] = (byte)(value >> 24);
    }

    private static uint ReadUInt32(byte[] buffer, int offset) =>
        (uint)(buffer[offset] | buffer[offset + 1] << 8 | buffer[offset + 2] << 16 | buffer[offset + 3] << 24);
}

/// <summary>Legacy Dahua discovery: 0xa3 request / 0xb3 answer on port 5050 with three sections.</summary>
public sealed class DahuaLegacyDiscoveryProtocol : UdpDiscoveryProtocolBase
{
    private const int Port = 5050;
    private const byte AnswerMagic = 0xb3;
    private const int HeaderSize = 0x78;

    private static readonly byte[] Request =
    {
        0xa3, 0x01, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
    };

    public DahuaLegacyDiscoveryProtocol(ILogger<DahuaLegacyDiscoveryProtocol> logger) : base(logger) { }

    public override string ProtocolName => "Dahua (legacy)";
    protected override string VendorName => "Dahua";
    protected override int RequestPort => Port;
    protected override TimeSpan ListenWindow => TimeSpan.FromSeconds(2.5);

    protected override byte[] BuildRequest() => Request;

    protected override void Parse(byte[] data, IPEndPoint from, List<DiscoveredDevice> found)
    {
        if (data.Length < HeaderSize || data[0] != AnswerMagic) return;

        int section2Length = data[0x02];
        var section3Length = (int)ReadUInt16(data, 0x14);

        if (HeaderSize + section2Length + section3Length != data.Length) return;

        var ip = $"{data[0x38]}.{data[0x39]}.{data[0x3A]}.{data[0x3B]}";
        if (ip.StartsWith("0.0.0.")) ip = from.Address.ToString();

        var model = FixedString(data, 0x28, 16);
        var serial = "";
        var firmware = "";

        var position = HeaderSize;
        var macField = FixedString(data, position, Math.Min(17, section2Length));
        if (macField.Length >= 11) serial = macField;
        position += Math.Min(17, section2Length);

        if (section2Length > 17)
            model = FixedString(data, position, section2Length - 17);

        position = HeaderSize + section2Length;

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (section3Length > 0)
        {
            var text = Encoding.UTF8.GetString(data, position, section3Length).TrimEnd('\0');
            foreach (var line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var colon = line.IndexOf(':');
                if (colon > 0) values[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
        }

        if (values.TryGetValue("SerialNo", out var serialFromSection)) serial = serialFromSection;
        if (values.TryGetValue("Device", out var deviceFromSection) && deviceFromSection.Length > 0) model = deviceFromSection;
        if (values.TryGetValue("Version", out var version)) firmware = version;

        // the MAC lives in section 2 even when section 3 later replaces the serial number
        var mac = macField.Length == 17 ? macField.Replace('-', ':').ToUpperInvariant() : "";

        var properties = new Dictionary<string, object>
        {
            ["mask"] = $"{data[0x3C]}.{data[0x3D]}.{data[0x3E]}.{data[0x3F]}",
            ["gateway"] = $"{data[0x40]}.{data[0x41]}.{data[0x42]}.{data[0x43]}",
            ["ipv6"] = values.TryGetValue("IPv6Addr", out var ipv6) ? ipv6 : ""
        };

        foreach (var pair in values) properties["field." + pair.Key] = pair.Value;

        found.Add(Device(ip, model, serial, mac, firmware, properties));
    }

    private static ushort ReadUInt16(byte[] buffer, int offset) =>
        (ushort)(buffer[offset] | buffer[offset + 1] << 8);
}

/// <summary>Vivotek: 5-byte header (session + magic 0x4a5d8f1c) then type/length/value records on port 10000.</summary>
public sealed class VivotekDiscoveryProtocol : UdpDiscoveryProtocolBase
{
    private const int Port = 10000;
    private const uint Magic = 0x4a5d8f1c;

    public VivotekDiscoveryProtocol(ILogger<VivotekDiscoveryProtocol> logger) : base(logger) { }

    public override string ProtocolName => "Vivotek";
    protected override string VendorName => "Vivotek";
    protected override int RequestPort => Port;
    protected override TimeSpan ListenWindow => TimeSpan.FromSeconds(3);

    protected override byte[] BuildRequest() => new byte[]
    {
        0x01,
        unchecked((byte)(Magic >> 24)), unchecked((byte)(Magic >> 16)),
        unchecked((byte)(Magic >> 8)), unchecked((byte)Magic)
    };

    protected override void Parse(byte[] data, IPEndPoint from, List<DiscoveredDevice> found)
    {
        if (data.Length < 6) return;

        var magic = (uint)(data[1] << 24 | data[2] << 16 | data[3] << 8 | data[4]);
        if (magic != Magic) return;

        var device = new VivotekRecord();
        ReadRecords(data, 5, data.Length, device, depth: 0);

        var ip = device.IpAddress ?? from.Address.ToString();

        var properties = new Dictionary<string, object>
        {
            ["longName"] = device.LongName ?? "",
            ["port"] = device.Port?.ToString(CultureInfo.InvariantCulture) ?? "",
            ["language"] = device.Language ?? "",
            ["session"] = data[0].ToString(CultureInfo.InvariantCulture)
        };

        found.Add(Device(ip, device.ShortName ?? device.LongName ?? "Vivotek", device.Serial ?? "",
            device.Mac ?? "", null, properties));
    }

    private void ReadRecords(byte[] data, int start, int end, VivotekRecord record, int depth)
    {
        var position = start;

        while (position + 2 <= end)
        {
            var type = data[position];
            var length = data[position + 1];
            position += 2;

            if (position + length > end) return;

            var value = data.AsSpan(position, length);
            position += length;

            switch (type)
            {
                case 0x01:
                    record.LongName = Encoding.UTF8.GetString(value).TrimEnd('\0');
                    break;
                case 0x02 when length >= 6:
                    record.Mac = string.Join(':', value[..6].ToArray().Select(b => b.ToString("X2")));
                    break;
                case 0x03 when length >= 4:
                    record.IpAddress = $"{value[0]}.{value[1]}.{value[2]}.{value[3]}";
                    break;
                case 0x04 when depth == 0:
                    // a container with another block of records inside
                    ReadRecords(data, position - length, position, record, depth + 1);
                    break;
                case 0x06 when length >= 2:
                    record.Port = (ushort)(value[0] << 8 | value[1]);
                    break;
                case 0x08 when length >= 2:
                    record.Language = Encoding.UTF8.GetString(value);
                    break;
                case 0x09:
                    record.ShortName = Encoding.UTF8.GetString(value).TrimEnd('\0');
                    break;
                case 0x10 when length >= 4:
                    record.Serial = value[2..4].ToArray().Aggregate("", (acc, b) => acc + b.ToString("X2"));
                    break;
            }
        }
    }

    private sealed class VivotekRecord
    {
        public string? LongName;
        public string? ShortName;
        public string? Mac;
        public string? IpAddress;
        public string? Serial;
        public string? Language;
        public ushort? Port;
    }
}

/// <summary>
/// Ubiquiti: 4-byte little-endian probe on 10001 (broadcast and 233.89.188.1), reply is a 1-byte-type /
/// 2-byte little-endian tag with a 1-byte length prefix.
/// </summary>
public sealed class UbiquitiDiscoveryProtocol : UdpDiscoveryProtocolBase
{
    private const int Port = 10001;

    public UbiquitiDiscoveryProtocol(ILogger<UbiquitiDiscoveryProtocol> logger) : base(logger) { }

    public override string ProtocolName => "Ubiquiti";
    protected override string VendorName => "Ubiquiti";
    protected override int RequestPort => Port;
    protected override IPAddress[] RequestAddresses => new[]
    {
        IPAddress.Parse("233.89.188.1"),
        IPAddress.Broadcast
    };

    protected override byte[] BuildRequest() => new byte[] { 0x01, 0x00, 0x00, 0x00 };

    protected override void Parse(byte[] data, IPEndPoint from, List<DiscoveredDevice> found)
    {
        if (data.Length < 5) return;

        string? mac = null, ip = null, brand = null, model = null, firmware = null, hardware = null;
        var position = 4; // magic + declared size

        while (position + 3 <= data.Length)
        {
            var type = (ushort)(data[position] | data[position + 1] << 8);
            var length = data[position + 2];
            position += 3;

            if (position + length > data.Length) break;

            var value = data.AsSpan(position, length);
            position += length;

            switch (type)
            {
                case 0x0002 when length >= 10:
                    mac = string.Join(':', value[..6].ToArray().Select(b => b.ToString("X2")));
                    ip = $"{value[6]}.{value[7]}.{value[8]}.{value[9]}";
                    break;
                case 0x0001 or 0x0013 when length == 6:
                    mac ??= string.Join(':', value.ToArray().Select(b => b.ToString("X2")));
                    break;
                case 0x0003:
                    firmware = Encoding.UTF8.GetString(value).TrimEnd('\0');
                    break;
                case 0x000b:
                    brand = Encoding.UTF8.GetString(value).TrimEnd('\0');
                    break;
                case 0x000c or 0x0015:
                    model ??= Encoding.UTF8.GetString(value).TrimEnd('\0');
                    break;
                case 0x0016:
                    firmware = Encoding.UTF8.GetString(value).TrimEnd('\0');
                    break;
                case 0x001b:
                    hardware = Encoding.UTF8.GetString(value).TrimEnd('\0');
                    break;
            }
        }

        if (ip == null || !IPAddress.TryParse(ip, out _)) ip = from.Address.ToString();
        if (mac == null && model == null) return;

        var properties = new Dictionary<string, object>
        {
            ["brand"] = brand ?? "",
            ["hardware"] = hardware ?? ""
        };

        var device = Device(ip, model ?? brand ?? "Ubiquiti", "", mac ?? "", firmware, properties);
        found.Add(device with { HardwareVersion = hardware });
    }
}

/// <summary>Hanwha (Samsung) Techwin: 16-byte request on 7701, fixed-layout reply to port 7711.</summary>
public sealed class HanwhaDiscoveryProtocol : UdpDiscoveryProtocolBase
{
    private const int RequestPortNumber = 7701;
    private const int AnswerPortNumber = 7711;
    private const byte RequestType = 0x01;
    private const byte ReplyType = 0x0b;

    public HanwhaDiscoveryProtocol(ILogger<HanwhaDiscoveryProtocol> logger) : base(logger) { }

    public override string ProtocolName => "Hanwha Techwin";
    protected override string VendorName => "Hanwha";
    protected override int RequestPort => RequestPortNumber;
    protected override int? ReplyPort => AnswerPortNumber;

    protected override byte[] BuildRequest()
    {
        var request = new byte[16];
        request[0] = RequestType;
        return request;
    }

    protected override void Parse(byte[] data, IPEndPoint from, List<DiscoveredDevice> found)
    {
        if (data.Length < 0xA0 || data[0] != ReplyType) return;

        var mac = FixedString(data, 0x13, 17);
        var ip = FixedString(data, 0x25, 16);
        var mask = FixedString(data, 0x35, 16);
        var gateway = FixedString(data, 0x45, 16);
        var model = FixedString(data, 0x6d, 32);
        var webUrl = FixedString(data, 0x85, 64);

        if (!IPAddress.TryParse(ip, out _)) ip = from.Address.ToString();

        var properties = new Dictionary<string, object>
        {
            ["mask"] = mask,
            ["gateway"] = gateway,
            ["webInterface"] = webUrl
        };

        var device = Device(ip, model.Length > 0 ? model : "Hanwha", "", mac.Replace('-', ':').ToUpperInvariant(),
            null, properties);

        found.Add(device with
        {
            WebInterfaceUrl = Uri.TryCreate(webUrl, UriKind.Absolute, out var uri) ? uri : null
        });
    }
}