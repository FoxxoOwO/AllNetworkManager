namespace AllNetworkManager.Discovery.Protocols;

using AllNetworkManager.Core.Devices;
using AllNetworkManager.Core.Discovery;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

/// <summary>Microsoft SQL Server Browser: one 0x02 byte to 1434, CSVs of key;value;key;value…</summary>
public sealed class MsSqlBrowserDiscoveryProtocol : UdpDiscoveryProtocolBase
{
    private const int Port = 1434;

    public MsSqlBrowserDiscoveryProtocol(ILogger<MsSqlBrowserDiscoveryProtocol> logger) : base(logger) { }

    public override string ProtocolName => "MSSQL Browser";
    protected override string VendorName => "Microsoft";
    protected override int RequestPort => Port;
    protected override TimeSpan ListenWindow => TimeSpan.FromSeconds(2.5);

    protected override byte[] BuildRequest() => new byte[] { 0x02 };

    protected override void Parse(byte[] data, IPEndPoint from, List<DiscoveredDevice> found)
    {
        if (data.Length < 4 || data[0] != 0x05) return;

        // 3-byte header (type + little-endian length), then "ServerName;NAME;InstanceName;…"
        var text = Encoding.UTF8.GetString(data, 3, data.Length - 3).Trim('\0', '\r', '\n', ' ');

        var fields = text.Split(';', StringSplitOptions.TrimEntries);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i + 1 < fields.Length; i += 2)
            values[fields[i]] = fields[i + 1];

        if (values.Count == 0) return;

        var properties = new Dictionary<string, object>();
        foreach (var pair in values) properties["field." + pair.Key] = pair.Value;

        found.Add(Device(from.Address.ToString(),
            values.GetValueOrDefault("InstanceName", "SQL Server"),
            values.GetValueOrDefault("ServerName", ""),
            "",
            values.GetValueOrDefault("Version", ""), properties));
    }
}

/// <summary>Microchip/GCE (ELA, Eden access controllers): text probe, two text lines back.</summary>
public sealed class MicrochipDiscoveryProtocol : UdpDiscoveryProtocolBase
{
    private const int Port = 30303;
    private const string RequestMagic = "Discover GCE Devices";

    public MicrochipDiscoveryProtocol(ILogger<MicrochipDiscoveryProtocol> logger) : base(logger) { }

    public override string ProtocolName => "Microchip/GCE";
    protected override string VendorName => "Microchip";
    protected override int RequestPort => Port;
    protected override TimeSpan ListenWindow => TimeSpan.FromSeconds(2.5);

    protected override byte[] BuildRequest() => Encoding.UTF8.GetBytes(RequestMagic);

    protected override void Parse(byte[] data, IPEndPoint from, List<DiscoveredDevice> found)
    {
        var lines = Encoding.UTF8.GetString(data)
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToArray();

        if (lines.Length == 0) return;
        if (lines[0].Equals(RequestMagic, StringComparison.OrdinalIgnoreCase)) return;

        var name = lines.Length > 0 ? lines[0] : "GCE";
        var mac = lines.Length > 1 ? lines[1].Replace('-', ':').ToUpperInvariant() : "";

        found.Add(Device(from.Address.ToString(), name, "", mac, null,
            new Dictionary<string, object> { ["lines"] = string.Join(" | ", lines) }));
    }
}

/// <summary>360Vision: "DISCOVER\n", reply "DISCOVER TYPE=IPDOME KEY='mac' ID=serial".</summary>
public sealed class Vision360DiscoveryProtocol : UdpDiscoveryProtocolBase
{
    private const int Port = 3600;

    public Vision360DiscoveryProtocol(ILogger<Vision360DiscoveryProtocol> logger) : base(logger) { }

    public override string ProtocolName => "360Vision";
    protected override string VendorName => "360Vision";
    protected override int RequestPort => Port;
    protected override TimeSpan ListenWindow => TimeSpan.FromSeconds(2.5);

    protected override byte[] BuildRequest() => Encoding.UTF8.GetBytes("DISCOVER\n");

    protected override void Parse(byte[] data, IPEndPoint from, List<DiscoveredDevice> found)
    {
        var text = Encoding.UTF8.GetString(data).Trim('\0', '\r', '\n', ' ');
        if (!text.StartsWith("DISCOVER", StringComparison.OrdinalIgnoreCase)) return;

        string Field(string key)
        {
            var match = Regex.Match(text, key + @"\s*=\s*'?([^'\s]+)'?", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value : "";
        }

        var type = Field("TYPE");
        var mac = Field("KEY").Replace('-', ':').ToUpperInvariant();
        var id = Field("ID");

        if (mac.Length == 0 && type.Length == 0) return;

        found.Add(Device(from.Address.ToString(), type.Length > 0 ? type : "360Vision", id, mac, null,
            new Dictionary<string, object> { ["type"] = type }));
    }
}

/// <summary>Sony: 0x02-framed "KEY:value" records separated by 0xff on port 2380.</summary>
public sealed class SonyDiscoveryProtocol : UdpDiscoveryProtocolBase
{
    private const int Port = 2380;
    private const byte Start = 0x02;
    private const byte End = 0xff;

    public SonyDiscoveryProtocol(ILogger<SonyDiscoveryProtocol> logger) : base(logger) { }

    public override string ProtocolName => "Sony";
    protected override string VendorName => "Sony";
    protected override int RequestPort => Port;
    protected override TimeSpan ListenWindow => TimeSpan.FromSeconds(3);

    protected override byte[] BuildRequest() => new byte[] { Start }
        .Concat(Encoding.UTF8.GetBytes("ENQ:allinfo"))
        .Append(End)
        .ToArray();

    protected override void Parse(byte[] data, IPEndPoint from, List<DiscoveredDevice> found)
    {
        if (data.Length < 4) return;

        // 0xff is not valid UTF-8, so the records have to be cut out of the bytes, not out of the string
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var recordStart = 0;

        for (int i = 0; i < data.Length; i++)
        {
            if (data[i] != End) continue;

            var record = Encoding.UTF8.GetString(data, recordStart, i - recordStart)
                .Trim((char)Start)
                .Trim();
            recordStart = i + 1;

            var colon = record.IndexOf(':');
            if (colon <= 0) continue;

            values[record[..colon].Trim()] = record[(colon + 1)..].Trim();
        }

        var ip = values.GetValueOrDefault("IPADR", "");
        if (!IPAddress.TryParse(ip, out _)) ip = from.Address.ToString();

        var mac = values.GetValueOrDefault("MAC", "").Replace('-', ':').ToUpperInvariant();
        if (mac.Length == 0) return;

        var properties = new Dictionary<string, object>();
        foreach (var pair in values) properties["field." + pair.Key] = pair.Value;

        var device = Device(ip, values.GetValueOrDefault("MODEL", "Sony"), values.GetValueOrDefault("SERIAL", ""),
            mac, values.GetValueOrDefault("SOFTVER", ""), properties);

        var webPort = values.GetValueOrDefault("WEBPORT", "80");
        found.Add(device with
        {
            WebInterfaceUrl = Uri.TryCreate($"http://{ip}:{webPort}/", UriKind.Absolute, out var uri) ? uri : null
        });
    }
}

/// <summary>Eaton UPS: "&lt;SCAN_REQUEST/&gt;" to 4679, XML list of UPS.PowerSummary objects back.</summary>
public sealed class EatonDiscoveryProtocol : UdpDiscoveryProtocolBase
{
    private const int Port = 4679;

    public EatonDiscoveryProtocol(ILogger<EatonDiscoveryProtocol> logger) : base(logger) { }

    public override string ProtocolName => "Eaton UPS";
    protected override string VendorName => "Eaton";
    protected override int RequestPort => Port;
    protected override TimeSpan ListenWindow => TimeSpan.FromSeconds(2.5);

    protected override byte[] BuildRequest() => Encoding.UTF8.GetBytes("<SCAN_REQUEST/>");

    protected override void Parse(byte[] data, IPEndPoint from, List<DiscoveredDevice> found)
    {
        var text = Encoding.UTF8.GetString(data).Trim('\0', ' ', '\r', '\n');
        if (!text.Contains("<OBJECT", StringComparison.Ordinal)) return;

        try
        {
            var document = XDocument.Parse(text);
            var objects = document.Descendants("OBJECT")
                .Where(e => e.Attribute("name") != null)
                .ToDictionary(e => e.Attribute("name")!.Value, e => e.Value.Trim());

            string Get(string suffix) =>
                objects.FirstOrDefault(p => p.Key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)).Value ?? "";

            var product = Get("iProduct");
            var model = Get("iModel");
            var serial = Get("iSerialNumber");
            var version = Get("iVersion");

            if (product.Length == 0 && model.Length == 0) return;

            var properties = new Dictionary<string, object>();
            foreach (var pair in objects.Take(24)) properties["field." + pair.Key] = pair.Value;

            found.Add(Device(from.Address.ToString(), model.Length > 0 ? model : product, serial, "", version, properties));
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Eaton: malformed XML from {From}", from);
        }
    }
}

/// <summary>Eden Optima (access control): "DETECT BOX" to 8088, "BOX|&lt;root&gt;…" with network settings.</summary>
public sealed class EdenOptimaDiscoveryProtocol : UdpDiscoveryProtocolBase
{
    private const int Port = 8088;

    public EdenOptimaDiscoveryProtocol(ILogger<EdenOptimaDiscoveryProtocol> logger) : base(logger) { }

    public override string ProtocolName => "Eden Optima";
    protected override string VendorName => "Eden";
    protected override int RequestPort => Port;
    protected override int? ReplyPort => Port;
    protected override TimeSpan ListenWindow => TimeSpan.FromSeconds(2.5);

    protected override byte[] BuildRequest() => Encoding.UTF8.GetBytes("DETECT BOX");

    protected override void Parse(byte[] data, IPEndPoint from, List<DiscoveredDevice> found)
    {
        var text = Encoding.UTF8.GetString(data).Trim('\0', ' ', '\r', '\n');
        var start = text.IndexOf('<');
        if (start < 0 || !text.Contains("root", StringComparison.Ordinal)) return;

        try
        {
            var document = XDocument.Parse(text[start..].TrimEnd('\0'));

            string Get(string name) => document.Descendants(name).FirstOrDefault()?.Value.Trim() ?? "";

            var ip = Get("adresseIP");
            if (!IPAddress.TryParse(ip, out _)) ip = from.Address.ToString();

            var properties = new Dictionary<string, object>
            {
                ["mask"] = Get("masqueSousReseau"),
                ["gateway"] = Get("passerelle"),
                ["dns1"] = Get("DNS1"),
                ["dns2"] = Get("DNS2"),
                ["dhcp"] = Get("dhcp"),
                ["webPort"] = Get("portWeb")
            };

            var webPort = Get("portWeb");
            var device = Device(ip, "Eden Optima", "", Get("serialNumber").Replace('-', ':'), null, properties);

            found.Add(device with
            {
                WebInterfaceUrl = Uri.TryCreate(
                    webPort.Length > 0 && webPort != "80" ? $"http://{ip}:{webPort}/" : $"http://{ip}/",
                    UriKind.Absolute, out var uri) ? uri : null
            });
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Eden Optima: malformed XML from {From}", from);
        }
    }
}

/// <summary>Lantronix (Vauban access control, Eden sub-controllers): 4-byte 0xf6 probe to 30718.</summary>
public sealed class LantronixDiscoveryProtocol : UdpDiscoveryProtocolBase
{
    private const int Port = 30718;
    private const byte ReplyType = 0xf7;

    public LantronixDiscoveryProtocol(ILogger<LantronixDiscoveryProtocol> logger) : base(logger) { }

    public override string ProtocolName => "Lantronix";
    protected override string VendorName => "Lantronix";
    protected override int RequestPort => Port;
    protected override TimeSpan ListenWindow => TimeSpan.FromSeconds(2.5);

    protected override byte[] BuildRequest() => new byte[] { 0x00, 0x00, 0x00, 0xf6 };

    protected override void Parse(byte[] data, IPEndPoint from, List<DiscoveredDevice> found)
    {
        if (data.Length != 0x1E || data[3] != ReplyType) return;

        var gateway = $"{data[0x10]}.{data[0x11]}.{data[0x12]}.{data[0x13]}";
        var mac = Mac(data, 0x18);
        if (mac.Length == 0 || mac.StartsWith("00:00:00")) mac = Mac(data, 0x18);

        var hardware = $"{data[0x08]}.{data[0x09]}";
        var version = (ushort)(data[0x0E] | data[0x0F] << 8);

        found.Add(Device(from.Address.ToString(), "Lantronix", "", mac, version.ToString(CultureInfo.InvariantCulture),
            new Dictionary<string, object>
            {
                ["gateway"] = gateway,
                ["hardware"] = hardware
            }));
    }
}

/// <summary>Advantech: 60-byte "MADA" probe to 5048, reply header + product info block.</summary>
public sealed class AdvantechDiscoveryProtocol : UdpDiscoveryProtocolBase
{
    private const int Port = 5048;
    private const uint Magic = 0x4144414D; // "MADA"
    private const int HeaderSize = 0x38;

    private static readonly byte[] Request =
    {
        0x4d, 0x41, 0x44, 0x41, 0x00, 0x00, 0x00, 0x83, 0x01, 0x00, 0x50, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x20, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
    };

    public AdvantechDiscoveryProtocol(ILogger<AdvantechDiscoveryProtocol> logger) : base(logger) { }

    public override string ProtocolName => "Advantech";
    protected override string VendorName => "Advantech";
    protected override int RequestPort => Port;
    protected override TimeSpan ListenWindow => TimeSpan.FromSeconds(2.5);

    protected override byte[] BuildRequest() => Request;

    protected override void Parse(byte[] data, IPEndPoint from, List<DiscoveredDevice> found)
    {
        if (data.Length < HeaderSize) return;
        if (BitConverter.ToUInt32(data, 0) != Magic) return;

        var mac = Mac(data, 0x0D);
        var hardware = $"{data[0x08]}.{data[0x0A]}";

        var model = data.Length > HeaderSize + 0x32
            ? FixedString(data, HeaderSize + 0x32, data.Length - HeaderSize - 0x32)
            : "";

        if (model.Length == 0) model = "Advantech";

        found.Add(Device(from.Address.ToString(), model, "", mac, null,
            new Dictionary<string, object> { ["hardware"] = hardware }));
    }
}

/// <summary>Vstarcam: 4-byte magic 0x44480101 to 8600, 256-byte fixed-layout reply.</summary>
public sealed class VstarcamDiscoveryProtocol : UdpDiscoveryProtocolBase
{
    private const int Port = 8600;
    private const int AnswerSize = 0x100;

    public VstarcamDiscoveryProtocol(ILogger<VstarcamDiscoveryProtocol> logger) : base(logger) { }

    public override string ProtocolName => "Vstarcam";
    protected override string VendorName => "Vstarcam";
    protected override int RequestPort => Port;
    protected override TimeSpan ListenWindow => TimeSpan.FromSeconds(3);

    protected override byte[] BuildRequest() => new byte[] { 0x44, 0x48, 0x01, 0x01 };

    protected override void Parse(byte[] data, IPEndPoint from, List<DiscoveredDevice> found)
    {
        if (data.Length < AnswerSize) return;

        var ip = FixedString(data, 0x04, 16);
        if (!IPAddress.TryParse(ip, out _)) ip = from.Address.ToString();

        var properties = new Dictionary<string, object>
        {
            ["mask"] = FixedString(data, 0x14, 16),
            ["gateway"] = FixedString(data, 0x24, 16),
            ["dns1"] = FixedString(data, 0x34, 16),
            ["dns2"] = FixedString(data, 0x44, 16)
        };

        found.Add(Device(ip, FixedString(data, 0x7C, 32), FixedString(data, 0x5C, 32),
            Mac(data, 0x54), FixedString(data, 0xCC, 32), properties));
    }
}

/// <summary>NiceVision: "NICE" request on 2007 telling the device which port to answer on.</summary>
public sealed class NiceVisionDiscoveryProtocol : UdpDiscoveryProtocolBase
{
    private const int Port = 2007;
    private static readonly byte[] Magic = { 0x4E, 0x49, 0x43, 0x45 }; // "NICE"

    public NiceVisionDiscoveryProtocol(ILogger<NiceVisionDiscoveryProtocol> logger) : base(logger) { }

    public override string ProtocolName => "NiceVision";
    protected override string VendorName => "NiceVision";
    protected override int RequestPort => Port;
    protected override TimeSpan ListenWindow => TimeSpan.FromSeconds(2.5);

    protected override byte[] BuildRequest() => BuildRequest(0);

    protected override byte[] BuildRequest(int localPort)
    {
        var request = new byte[14];
        Magic.CopyTo(request, 0);
        request[4] = 0x00;
        request[5] = 0x01;                                    // transaction id, big endian
        request[10] = (byte)(localPort >> 8);                  // answer-to port, big endian
        request[11] = (byte)localPort;
        return request;
    }

    protected override void Parse(byte[] data, IPEndPoint from, List<DiscoveredDevice> found)
    {
        if (data.Length < 0x5A) return;
        if (data[0] != Magic[0] || data[1] != Magic[1] || data[2] != Magic[2] || data[3] != Magic[3]) return;

        var ip = $"{data[0x10]}.{data[0x11]}.{data[0x12]}.{data[0x13]}";
        if (ip.StartsWith("0.0.0.")) ip = from.Address.ToString();

        var version = string.Join('.', new[] { data[0x1E], data[0x20], data[0x22], data[0x24] });

        var properties = new Dictionary<string, object>
        {
            ["mask"] = $"{data[0x14]}.{data[0x15]}.{data[0x16]}.{data[0x17]}",
            ["gateway"] = $"{data[0x18]}.{data[0x19]}.{data[0x1A]}.{data[0x1B]}"
        };

        found.Add(Device(ip, FixedString(data, 0x4A, 16), "", Mac(data, 0x0A), version, properties));
    }
}