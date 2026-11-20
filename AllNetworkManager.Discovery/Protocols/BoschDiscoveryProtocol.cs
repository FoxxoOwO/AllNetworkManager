namespace AllNetworkManager.Discovery.Protocols;

using AllNetworkManager.Core.Devices;
using AllNetworkManager.Core.Discovery;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>Bosch: UDP 1757 request, reply on 1758. Binary (32-byte) or XML/SSDP response.</summary>
public sealed class BoschDiscoveryProtocol : UdpDiscoveryProtocolBase
{
    private const int RequestPortNumber = 1757;
    private const int AnswerPortNumber = 1758;
    private const uint RequestMagic = 0xff0006de;
    private const uint AnswerMagic = 0x9939a427;
    private const int BinaryReplySize = 0x20; // 32 bytes

    public BoschDiscoveryProtocol(ILogger<BoschDiscoveryProtocol> logger) : base(logger) { }

    public override string ProtocolName => "Bosch";
    protected override string VendorName => "Bosch";
    protected override int RequestPort => RequestPortNumber;
    protected override int? ReplyPort => AnswerPortNumber;

    protected override byte[] BuildRequest()
    {
        var request = new byte[12]; // BoschRequest struct size
        var transactionId = GenerateTransactionId();

        // Magic (big endian)
        WriteUInt32BigEndian(request, 0, AnswerMagic);
        // Transaction ID (big endian)
        WriteUInt32BigEndian(request, 4, transactionId);
        // Request magic (big endian)
        WriteUInt32BigEndian(request, 8, RequestMagic);

        return request;
    }

    protected override void Parse(byte[] data, IPEndPoint from, List<DiscoveredDevice> found)
    {
        // Check for binary reply (32 bytes)
        if (data.Length == BinaryReplySize)
        {
            if (ReadUInt32BigEndian(data, 0) != AnswerMagic) return;

            // MAC at offset 0x08 (8) - 6 bytes
            var mac = Mac(data, 8);
            // IPv4 at offset 0x10 (16) - 4 bytes, little endian
            var ip = $"{data[16]}.{data[17]}.{data[18]}.{data[19]}";
            // Mask at offset 0x14 (20) - 4 bytes, little endian
            var mask = $"{data[20]}.{data[21]}.{data[22]}.{data[23]}";
            // Gateway at offset 0x18 (24) - 4 bytes, little endian
            var gateway = $"{data[24]}.{data[25]}.{data[26]}.{data[27]}";

            if (!IPAddress.TryParse(ip, out _)) ip = from.Address.ToString();

            var properties = new Dictionary<string, object>
            {
                ["mask"] = mask,
                ["gateway"] = gateway,
                ["discoveryMode"] = "binary"
            };

            var device = Device(ip, "Bosch", "", mac, null, properties);
            found.Add(device);
            return;
        }

        // XML/SSDP reply - parse with regex like UniversalScanner
        var xml = Encoding.UTF8.GetString(data).Trim('\0', '\r', '\n', ' ');

        var model = ExtractXmlValue(xml, "friendlyName");
        var ipStr = ExtractXmlValue(xml, "unitIPAddress");
        var ipv6Str = ExtractXmlValue(xml, "unitIPv6Address");
        var macStr = ExtractXmlValue(xml, "physAddress");
        var serialStr = ExtractXmlValue(xml, "serialNumber");

        if (string.IsNullOrWhiteSpace(macStr) && !string.IsNullOrWhiteSpace(serialStr))
            macStr = serialStr;

        if (!string.IsNullOrWhiteSpace(ipStr) && IPAddress.TryParse(ipStr, out var parsedIp))
        {
            var properties = new Dictionary<string, object>
            {
                ["discoveryMode"] = "xml"
            };
            if (!string.IsNullOrWhiteSpace(ipv6Str)) properties["ipv6"] = ipv6Str;

            var device = Device(ipStr, model.Length > 0 ? model : "Bosch", serialStr,
                NormalizeMac(macStr), null, properties);
            found.Add(device);
        }

        if (!string.IsNullOrWhiteSpace(ipv6Str) && IPAddress.TryParse(ipv6Str, out var parsedIpv6))
        {
            var properties = new Dictionary<string, object>
            {
                ["discoveryMode"] = "xml",
                ["ipv6"] = ipv6Str
            };

            var device = Device(ipv6Str, model.Length > 0 ? model : "Bosch", serialStr,
                NormalizeMac(macStr), null, properties);
            found.Add(device);
        }
    }

    private static uint GenerateTransactionId()
    {
        var now = DateTime.UtcNow;
        return (uint)((now.Hour << 24) | (now.Minute << 16) | (now.Second << 8) | (now.Millisecond / 10));
    }

    private static uint ReadUInt32BigEndian(byte[] data, int offset)
    {
        return (uint)(data[offset] << 24 | data[offset + 1] << 16 | data[offset + 2] << 8 | data[offset + 3]);
    }

    private static void WriteUInt32BigEndian(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)(value >> 24);
        data[offset + 1] = (byte)(value >> 16);
        data[offset + 2] = (byte)(value >> 8);
        data[offset + 3] = (byte)value;
    }

    private static string ExtractXmlValue(string xml, string tag)
    {
        var match = Regex.Match(xml,
            "<" + tag + ">([^<]*)</" + tag + ">",
            RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim() : "";
    }

    private static string NormalizeMac(string mac)
    {
        if (string.IsNullOrWhiteSpace(mac)) return "";
        return mac.Replace('-', ':').ToUpperInvariant();
    }
}