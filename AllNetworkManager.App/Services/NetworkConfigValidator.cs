namespace AllNetworkManager.App.Services;

using System.Net;
using System.Net.Sockets;
using AllNetworkManager.Core.Vendors;

/// <summary>
/// Checks a network configuration before it is written to a device. A wrong address or mask locks the
/// device out of the network, so this runs before every apply — not only in the UI.
/// </summary>
public static class NetworkConfigValidator
{
    public static bool TryValidate(NetworkConfig config, out string problem)
    {
        problem = "";

        if (config.IsDhcp)
        {
            if (!string.IsNullOrWhiteSpace(config.Hostname) && !IsValidHostname(config.Hostname))
            {
                problem = "Hostname smí obsahovat jen písmena, číslice a pomlčky (max 63 znaků).";
                return false;
            }

            return true;
        }

        if (!TryParseIPv4(config.IpAddress, out var address))
        {
            problem = $"IP adresa '{config.IpAddress}' není platná IPv4 adresa.";
            return false;
        }

        if (!TryParseIPv4(config.SubnetMask, out var mask) || !IsContiguousMask(mask))
        {
            problem = $"Maska '{config.SubnetMask}' není platná (např. 255.255.255.0).";
            return false;
        }

        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.Broadcast) || address.AddressFamily != AddressFamily.InterNetwork)
        {
            problem = "IP adresa nesmí být 0.0.0.0 ani broadcast.";
            return false;
        }

        if (IsNetworkAddress(address, mask))
        {
            problem = "IP adresa nesmí být adresa sítě.";
            return false;
        }

        if (IsBroadcastAddress(address, mask))
        {
            problem = "IP adresa nesmí být broadcast adresa podsítě.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(config.Gateway))
        {
            if (!TryParseIPv4(config.Gateway, out var gateway))
            {
                problem = $"Brána '{config.Gateway}' není platná IPv4 adresa.";
                return false;
            }

            if (!IsInSameSubnet(gateway, address, mask))
            {
                problem = $"Brána {config.Gateway} není ve stejné podsíti jako {config.IpAddress}/{config.SubnetMask}.";
                return false;
            }

            if (gateway.Equals(address))
            {
                problem = "Brána nesmí být stejná jako IP adresa zařízení.";
                return false;
            }
        }

        foreach (var dns in new[] { config.Dns1, config.Dns2 })
        {
            if (string.IsNullOrWhiteSpace(dns)) continue;

            if (!TryParseIPv4(dns, out _))
            {
                problem = $"DNS server '{dns}' není platná IPv4 adresa.";
                return false;
            }
        }

        if (config.VlanId is < 1 or > 4094)
        {
            problem = "VLAN ID musí být 1–4094.";
            return false;
        }

        return true;
    }

    /// <summary>Strict dotted quad: IPAddress.TryParse alone accepts "8.8.8" and "192.168.1".</summary>
    private static bool TryParseIPv4(string? value, out IPAddress address)
    {
        address = IPAddress.None;

        if (string.IsNullOrWhiteSpace(value)) return false;

        var parts = value.Trim().Split('.');
        if (parts.Length != 4) return false;

        var bytes = new byte[4];
        for (int i = 0; i < 4; i++)
        {
            if (parts[i].Length is 0 or > 3) return false;
            if (!parts[i].All(char.IsAsciiDigit)) return false;
            if (!byte.TryParse(parts[i], out bytes[i])) return false;
        }

        address = new IPAddress(bytes);
        return true;
    }

    private static bool IsBroadcastAddress(IPAddress address, IPAddress mask)
        => ToUInt(address) == (ToUInt(Network(address, mask)) | ~ToUInt(mask));

    private static bool IsContiguousMask(IPAddress mask)
    {
        uint bits = ToUInt(mask);
        uint inverted = ~bits;
        return (inverted & (inverted + 1)) == 0; // ones only on the left
    }

    private static uint ToUInt(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return (uint)(bytes[0] << 24 | bytes[1] << 16 | bytes[2] << 8 | bytes[3]);
    }

    // IPAddress(long) re-interprets the value, which flipped the byte order here; build the bytes explicitly.
    private static IPAddress FromUInt(uint value) => new(new[]
    {
        (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value
    });

    private static IPAddress Network(IPAddress address, IPAddress mask)
        => FromUInt(ToUInt(address) & ToUInt(mask));

    private static bool IsNetworkAddress(IPAddress address, IPAddress mask)
        => ToUInt(address) == ToUInt(Network(address, mask));

    private static bool IsInSameSubnet(IPAddress a, IPAddress b, IPAddress mask)
        => ToUInt(Network(a, mask)) == ToUInt(Network(b, mask));

    private static bool IsValidHostname(string hostname)
        => hostname.Length <= 63
           && hostname.All(c => char.IsLetterOrDigit(c) || c == '-' || c == '.')
           && !hostname.StartsWith('-')
           && !hostname.EndsWith('-');
}
