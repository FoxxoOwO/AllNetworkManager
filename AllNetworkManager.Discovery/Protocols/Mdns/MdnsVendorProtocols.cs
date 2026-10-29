namespace AllNetworkManager.Discovery.Protocols.Mdns;

using AllNetworkManager.Core.Devices;
using AllNetworkManager.Core.Discovery;
using Microsoft.Extensions.Logging;

/// <summary>Axis cameras and NVRs advertise themselves over DNS-SD (<c>_axis-video</c>, <c>_axis-nvr</c>).</summary>
public sealed class AxisMdnsDiscoveryProtocol : MdnsServiceDiscoveryProtocol
{
    public AxisMdnsDiscoveryProtocol(ILogger<AxisMdnsDiscoveryProtocol> logger) : base(logger) { }

    public override string ProtocolName => "Axis mDNS";

    protected override string[] Services => new[]
    {
        "_axis-video._tcp.local",
        "_axis-nvr._tcp.local",
        "_axis-disc._tcp.local"
    };

    protected override DiscoveredDevice? ToDevice(MdnsServiceInstance instance)
    {
        // instance names look like "AXIS P3265-LVE - B8A44F123456._axis-video._tcp.local"
        var friendly = StripService(instance.InstanceName, instance.Service);
        var dash = friendly.LastIndexOf(" - ", StringComparison.Ordinal);

        var model = instance.TxtValue("model", "product", "modelname", "ty");
        if (model.Length == 0)
            model = dash > 0 ? friendly[..dash].Trim() : friendly;

        var serial = instance.TxtValue("serial", "serialnumber", "sn");
        if (serial.Length == 0 && dash > 0) serial = friendly[(dash + 3)..].Trim();

        var mac = NormalizeMac(instance.TxtValue("macaddress", "mac", "macaddr"));

        var properties = new Dictionary<string, object>
        {
            ["service"] = instance.Service,
            ["host"] = instance.Host,
            ["port"] = instance.Port.ToString(),
            ["instance"] = friendly
        };

        foreach (var pair in instance.Txt) properties["txt." + pair.Key] = pair.Value;

        var isNvr = instance.Service.Contains("nvr", StringComparison.OrdinalIgnoreCase);

        var device = CreateDevice(instance, model.Length > 0 ? model : "AXIS",
            serial, mac, instance.TxtValue("version", "fw", "firmware"),
            isNvr ? DeviceType.Recorder : DeviceType.Camera, properties);

        return device with
        {
            WebInterfaceUrl = Uri.TryCreate($"http://{instance.Address}/", UriKind.Absolute, out var uri) ? uri : null
        };
    }

    private static string StripService(string name, string service)
        => service.Length > 0 && name.EndsWith(service, StringComparison.OrdinalIgnoreCase)
            ? name[..^service.Length].TrimEnd('.')
            : name;

    internal static string NormalizeMac(string value)
    {
        if (value.Length == 0) return "";

        var digits = new string(value.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        if (digits.Length != 12) return value;

        return string.Join(':', Enumerable.Range(0, 6).Select(i => digits.Substring(i * 2, 2)));
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