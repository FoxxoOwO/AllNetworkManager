namespace AllNetworkManager.Discovery.Protocols.Mdns;

using AllNetworkManager.Core.Devices;
using AllNetworkManager.Core.Discovery;
using Microsoft.Extensions.Logging;

/// <summary>Axis cameras, NVRs, audio and RTSP devices advertise themselves over DNS-SD.</summary>
public sealed class AxisMdnsDiscoveryProtocol : MdnsServiceDiscoveryProtocol
{
    public AxisMdnsDiscoveryProtocol(ILogger<AxisMdnsDiscoveryProtocol> logger) : base(logger) { }

    public override string ProtocolName => "Axis mDNS";

    protected override string[] Services => new[]
    {
        "_axis-video._tcp.local",
        "_axis-nvr._tcp.local",
        "_axis-disc._tcp.local",
        "_axis-rtsp._tcp.local",
        "_axis-audio._tcp.local"
    };

    protected override DiscoveredDevice? ToDevice(MdnsServiceInstance instance)
    {
        // instance names look like "AXIS P3265-LVE - B8A44F123456._axis-video._tcp.local"
        var friendly = StripService(instance.InstanceName, instance.Service);

        // Try multiple delimiter patterns
        var model = instance.TxtValue("model", "product", "modelname", "ty");
        var serial = instance.TxtValue("serial", "serialnumber", "sn");
        
        if (model.Length == 0 || serial.Length == 0)
        {
            // Try " - " delimiter first (most common)
            var dash = friendly.LastIndexOf(" - ", StringComparison.Ordinal);
            if (dash > 0)
            {
                if (model.Length == 0) model = friendly[..dash].Trim();
                if (serial.Length == 0) serial = friendly[(dash + 3)..].Trim();
            }
            else
            {
                // Try "_" delimiter (older firmware)
                var underscore = friendly.LastIndexOf('_');
                if (underscore > 0)
                {
                    if (model.Length == 0) model = friendly[..underscore].Trim();
                    if (serial.Length == 0) serial = friendly[(underscore + 1)..].Trim();
                }
                else if (model.Length == 0)
                {
                    // Fallback: whole name as model
                    model = friendly;
                }
            }
        }

        // Clean up model/serial - remove trailing dots from service suffix
        model = model.TrimEnd('.');
        serial = serial.TrimEnd('.');

        var mac = NormalizeMac(instance.TxtValue("macaddress", "mac", "macaddr", "ethmac"));

        var properties = new Dictionary<string, object>
        {
            ["service"] = instance.Service,
            ["host"] = instance.Host,
            ["port"] = instance.Port.ToString(),
            ["instance"] = friendly
        };

        foreach (var pair in instance.Txt) properties["txt." + pair.Key] = pair.Value;

        // Infer device type from service and model
        var deviceType = InferDeviceType(instance.Service, model);

        var firmware = instance.TxtValue("version", "fw", "firmware", "fv");
        var device = CreateDevice(instance, model.Length > 0 ? model : "AXIS",
            serial, mac, firmware, deviceType, properties);

        // Handle both IPv4 and IPv6 addresses
        string? address = instance.Address?.ToString();
        if (string.IsNullOrEmpty(address) && instance.Host.Length > 0)
        {
            // Try to resolve hostname
            try { address = instance.Host; } catch { }
        }

        return device with
        {
            WebInterfaceUrl = !string.IsNullOrEmpty(address) 
                ? Uri.TryCreate($"http://{address}/", UriKind.Absolute, out var uri) ? uri : null
                : null
        };
    }

    private static DeviceType InferDeviceType(string service, string model)
    {
        if (service.Contains("nvr", StringComparison.OrdinalIgnoreCase))
            return DeviceType.Recorder;
        if (service.Contains("audio", StringComparison.OrdinalIgnoreCase))
            return DeviceType.Other; // Audio device
        if (service.Contains("rtsp", StringComparison.OrdinalIgnoreCase))
            return DeviceType.Camera;
        if (service.Contains("disc", StringComparison.OrdinalIgnoreCase))
            return DeviceType.Camera;
        if (service.Contains("video", StringComparison.OrdinalIgnoreCase))
            return DeviceType.Camera;

        // Fallback: infer from model name
        var lowerModel = model.ToLowerInvariant();
        if (lowerModel.Contains("nvr") || lowerModel.Contains("recorder"))
            return DeviceType.Recorder;
        if (lowerModel.Contains("audio") || lowerModel.Contains("speaker"))
            return DeviceType.Other;

        return DeviceType.Camera;
    }

    private static string StripService(string name, string service)
        => service.Length > 0 && name.EndsWith(service, StringComparison.OrdinalIgnoreCase)
            ? name[..^service.Length].TrimEnd('.')
            : name;

    internal static string NormalizeMac(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";

        // Extract only hex digits
        var digits = new string(value.Where(c => char.IsLetterOrDigit(c)).ToArray()).ToUpperInvariant();
        
        // Handle common MAC formats: 12 hex, 17 with colons/dashes
        if (digits.Length == 12)
        {
            return string.Join(':', Enumerable.Range(0, 6).Select(i => digits.Substring(i * 2, 2)));
        }
        
        // Already formatted with colons/dashes - normalize to colons
        if (value.Contains(':') || value.Contains('-'))
        {
            return value.Replace('-', ':').ToUpperInvariant();
        }

        return value; // Return as-is if unrecognized format
    }
}

/// <summary>Google Cast (Chromecast, Android TV, Nest displays) via <c>_googlecast._tcp</c>.</summary>
public sealed class GoogleCastDiscoveryProtocol : MdnsServiceDiscoveryProtocol
{
    public GoogleCastDiscoveryProtocol(ILogger<GoogleCastDiscoveryProtocol> logger) : base(logger) { }

    public override string ProtocolName => "Google Cast";

    protected override string[] Services => new[] { "_googlecast._tcp.local" };

    protected override DiscoveredDevice? ToDevice(MdnsServiceInstance instance)
    {
        var friendly = StripService(instance.InstanceName);

        var model = instance.TxtValue("md", "model");
        var name = instance.TxtValue("fn", "friendlyname");
        var id = instance.TxtValue("id", "deviceid");
        var version = instance.TxtValue("ve", "version");

        var properties = new Dictionary<string, object>
        {
            ["friendlyName"] = name.Length > 0 ? name : friendly,
            ["deviceId"] = id,
            ["model"] = model,
            ["service"] = instance.Service,
            ["host"] = instance.Host,
            ["port"] = instance.Port.ToString()
        };

        foreach (var pair in instance.Txt) properties["txt." + pair.Key] = pair.Value;

        var device = CreateDevice(instance,
            model.Length > 0 ? model : (name.Length > 0 ? name : friendly),
            id, "", version, DeviceType.Other, properties);

        return device;
    }

    private static string StripService(string name)
    {
        var dot = name.IndexOf("._googlecast", StringComparison.OrdinalIgnoreCase);
        return dot > 0 ? name[..dot] : name;
    }
}

/// <summary>Arecont Vision cameras advertise <c>_arec._tcp</c>.</summary>
public sealed class ArecontMdnsDiscoveryProtocol : MdnsServiceDiscoveryProtocol
{
    public ArecontMdnsDiscoveryProtocol(ILogger<ArecontMdnsDiscoveryProtocol> logger) : base(logger) { }

    public override string ProtocolName => "Arecont mDNS";

    protected override string[] Services => new[] { "_arec._tcp.local" };

    protected override DiscoveredDevice? ToDevice(MdnsServiceInstance instance)
    {
        var friendly = instance.Service.Length > 0 && instance.InstanceName.EndsWith(instance.Service,
            StringComparison.OrdinalIgnoreCase)
            ? instance.InstanceName[..^instance.Service.Length].TrimEnd('.')
            : instance.InstanceName;

        var model = instance.TxtValue("model", "product");
        var serial = instance.TxtValue("serial", "serialnumber", "sn");
        var mac = AxisMdnsDiscoveryProtocol.NormalizeMac(instance.TxtValue("mac", "macaddress"));

        var properties = new Dictionary<string, object>
        {
            ["service"] = instance.Service,
            ["host"] = instance.Host,
            ["port"] = instance.Port.ToString(),
            ["instance"] = friendly
        };

        foreach (var pair in instance.Txt) properties["txt." + pair.Key] = pair.Value;

        return CreateDevice(instance, model.Length > 0 ? model : (friendly.Length > 0 ? friendly : "Arecont"),
            serial, mac, instance.TxtValue("version", "firmware"), DeviceType.Camera, properties);
    }
}