namespace AllNetworkManager.Core.Network;

using System.Net;
using System.Net.Sockets;

/// <summary>
/// Parses the scan targets a user can type: a CIDR subnet (<c>192.168.0.0/24</c>), an address range
/// (<c>192.168.0.10-192.168.0.60</c> or the shorthand <c>192.168.0.10-60</c>) or a single address.
/// </summary>
public static class AddressRange
{
    /// <summary>
    /// Upper bound so a typo like 10.0.0.0/8 cannot start a 16 million host sweep. 1024 covers a /22,
    /// which is the largest range that still finishes in a sensible time with 7 ports per address.
    /// </summary>
    public const int MaxAddresses = 1024;

    public static bool TryParse(string? text, out IReadOnlyList<string> addresses, out string error)
    {
        addresses = Array.Empty<string>();
        error = "";

        var value = (text ?? "").Trim();
        if (value.Length == 0)
        {
            error = "Zadej rozsah, například 192.168.0.1-192.168.0.254 nebo 10.0.0.0/24.";
            return false;
        }

        if (value.Contains('/'))
            return TryParseCidr(value, out addresses, out error);

        if (value.Contains('-'))
            return TryParseDashRange(value, out addresses, out error);

        if (TryParseIPv4(value, out var single))
        {
            addresses = new[] { single.ToString() };
            return true;
        }

        error = $"'{value}' není platná IP adresa, rozsah ani podsíť.";
        return false;
    }

    /// <summary>Number of hosts an address list would scan, without materializing it.</summary>
    public static bool TryParseCidr(string text, out IReadOnlyList<string> addresses, out string error)
    {
        addresses = Array.Empty<string>();
        error = "";

        var parts = text.Split('/');
        if (parts.Length != 2 || !TryParseIPv4(parts[0], out var network) || !int.TryParse(parts[1], out var prefix))
        {
            error = $"'{text}' není platná podsíť (očekávám třeba 192.168.0.0/24).";
            return false;
        }

        if (prefix is < 16 or > 32)
        {
            error = "Podsíť musí být v rozsahu /16 až /32 (větší sítě by trvaly příliš dlouho).";
            return false;
        }

        var baseAddress = ToUInt(network);
        var count = prefix == 32 ? 1u : 1u << (32 - prefix);
        var hostCount = count <= 2 ? count : count - 2;
        if (hostCount > MaxAddresses)
        {
            error = $"Podsíť /{prefix} má {hostCount} adres, maximum je {MaxAddresses}. Zadej menší rozsah.";
            return false;
        }

        var result = new List<string>();
        var first = count <= 2 ? 0u : 1u;
        var last = count <= 2 ? count : count - 1;
        for (var i = first; i < last; i++)
            result.Add(FromUInt(baseAddress + i).ToString());

        addresses = result;
        return true;
    }

    private static bool TryParseDashRange(string text, out IReadOnlyList<string> addresses, out string error)
    {
        addresses = Array.Empty<string>();
        error = "";

        var parts = text.Split('-', 2, StringSplitOptions.TrimEntries);
        if (!TryParseIPv4(parts[0], out var from))
        {
            error = $"'{parts[0]}' není platná počáteční adresa.";
            return false;
        }

        if (!TryParseIPv4(parts[1], out var to))
        {
            // shorthand form: 192.168.0.10-60
            var lastDot = parts[1].LastIndexOf('.');
            if (lastDot > 0 && TryParseIPv4(parts[1][..lastDot], out var full))
                to = full;
            else if (byte.TryParse(parts[1], out var lastOctet))
                to = FromUInt((ToUInt(from) & 0xFFFFFF00) + lastOctet);
            else
            {
                error = $"'{parts[1]}' není platná koncová adresa.";
                return false;
            }
        }

        var start = ToUInt(from);
        var end = ToUInt(to);
        if (end < start)
        {
            error = "Konec rozsahu je menší než začátek.";
            return false;
        }

        var total = end - start + 1;
        if (total > MaxAddresses)
        {
            error = $"Rozsah má {total} adres, maximum je {MaxAddresses}.";
            return false;
        }

        var result = new List<string>((int)total);
        for (var current = start; current <= end; current++)
            result.Add(FromUInt(current).ToString());

        addresses = result;
        return true;
    }

    private static bool TryParseIPv4(string? value, out IPAddress address)
    {
        address = IPAddress.None;

        if (string.IsNullOrWhiteSpace(value)) return false;

        var parts = value.Trim().Split('.');
        if (parts.Length != 4) return false;

        var bytes = new byte[4];
        for (int i = 0; i < 4; i++)
        {
            if (parts[i].Length is 0 or > 3 || !parts[i].All(char.IsAsciiDigit)) return false;
            if (!byte.TryParse(parts[i], out bytes[i])) return false;
        }

        address = new IPAddress(bytes);
        return address.AddressFamily == AddressFamily.InterNetwork;
    }

    private static uint ToUInt(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
    }

    private static IPAddress FromUInt(uint value) => new(new[]
    {
        (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value
    });

    /// <summary>Human readable summary for the UI, e.g. "254 adres".</summary>
    public static string Describe(IReadOnlyList<string> addresses) => addresses.Count switch
    {
        0 => "žádná adresa",
        1 => addresses[0],
        var n => $"{n} adres ({addresses[0]} … {addresses[^1]})"
    };
}