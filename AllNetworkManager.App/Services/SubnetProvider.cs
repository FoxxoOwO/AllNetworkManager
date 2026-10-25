namespace AllNetworkManager.App.Services;

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using AllNetworkManager.Core.Network;

/// <summary>One entry of the scan-target dropdown.</summary>
public sealed class SubnetOption
{
    private SubnetOption(string display, string? target, string interfaceName, bool isAuto, bool isCustom)
    {
        Display = display;
        Target = target;
        InterfaceName = interfaceName;
        IsAuto = isAuto;
        IsCustom = isCustom;
    }

    /// <summary>Text shown in the dropdown.</summary>
    public string Display { get; }

    /// <summary>CIDR subnet to scan, or null for "auto" / "custom".</summary>
    public string? Target { get; }

    public string InterfaceName { get; }
    public bool IsAuto { get; }
    public bool IsCustom { get; }

    /// <summary>Default entry: whatever the discovery service detects on its own.</summary>
    public static SubnetOption Auto { get; } =
        new("Všechna rozhraní (auto)", null, "", isAuto: true, isCustom: false);

    /// <summary>Entry that switches the UI to the free-text range field.</summary>
    public static SubnetOption Custom { get; } =
        new("Vlastní rozsah…", null, "", isAuto: false, isCustom: true);

    public static SubnetOption ForInterface(string name, string cidr, string localAddress, bool isUp) =>
        new($"{name} · {cidr}{(isUp ? "" : " (down)")}", cidr, name, isAuto: false, isCustom: false);

    public override string ToString() => Display;
}

/// <summary>Lists the IPv4 subnets of the local machine, so a scan can be limited to one network card.</summary>
public static class SubnetProvider
{
    public static IReadOnlyList<SubnetOption> Discover()
    {
        var options = new List<SubnetOption> { SubnetOption.Auto };

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces()
                         .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
            {
                var properties = nic.GetIPProperties();
                var isUp = nic.OperationalStatus == OperationalStatus.Up;

                foreach (var unicast in properties.UnicastAddresses
                             .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork))
                {
                    var mask = unicast.IPv4Mask;
                    if (mask == null || mask.Equals(IPAddress.Any)) continue;

                    var prefix = PrefixLength(mask);
                    if (prefix <= 0) continue;

                    // a /32 means a point-to-point or a misconfigured card: nothing to sweep
                    var cidr = $"{NetworkAddress(unicast.Address, mask)}/{prefix}";
                    options.Add(SubnetOption.ForInterface(nic.Name, cidr, unicast.Address.ToString(), isUp));
                }
            }
        }
        catch
        {
            // without network information the dropdown simply stays on "auto"
        }

        options.Add(SubnetOption.Custom);
        return options;
    }

    /// <summary>Validates a user-typed target and returns a short description for the status bar.</summary>
    public static bool TryDescribe(string? typed, out string description, out string error)
    {
        if (AddressRange.TryParse(typed, out var addresses, out error))
        {
            description = AddressRange.Describe(addresses);
            return true;
        }

        description = "";
        return false;
    }

    private static int PrefixLength(IPAddress mask)
    {
        var bytes = mask.GetAddressBytes();
        var prefix = 0;

        foreach (var b in bytes)
        {
            for (int bit = 7; bit >= 0; bit--)
            {
                if ((b & (1 << bit)) == 0) return prefix;
                prefix++;
            }
        }

        return prefix;
    }

    private static IPAddress NetworkAddress(IPAddress address, IPAddress mask)
    {
        var a = address.GetAddressBytes();
        var m = mask.GetAddressBytes();
        var network = new byte[4];

        for (int i = 0; i < 4; i++)
            network[i] = (byte)(a[i] & m[i]);

        return new IPAddress(network);
    }
}