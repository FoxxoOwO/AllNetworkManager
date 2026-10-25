namespace AllNetworkManager.App.Services;

using System.Net;

/// <summary>
/// Finds a device hostname: first from the discovery/vendor properties (every protocol calls it something
/// else), then — when nothing was reported — by reverse DNS.
/// </summary>
public static class HostnameResolver
{
    private static readonly string[] PropertyKeys =
    {
        "hostname", "HostName", "hostName", "Hostname",
        "microsens.hostname", "identity", "ServerName", "DeviceName", "name"
    };

    public static string? FromProperties(IDictionary<string, object>? properties)
    {
        if (properties == null) return null;

        foreach (var key in PropertyKeys)
        {
            if (properties.TryGetValue(key, out var value) && value is string text && IsUsable(text))
                return text.Trim();
        }

        // tolerates case differences coming from JSON round-trips
        foreach (var pair in properties)
        {
            if (pair.Value is not string text || !IsUsable(text)) continue;
            if (PropertyKeys.Any(k => k.Equals(pair.Key, StringComparison.OrdinalIgnoreCase)))
                return text.Trim();
        }

        return null;
    }

    /// <summary>Reverse DNS lookup with a short timeout; returns null instead of throwing.</summary>
    public static async Task<string?> ReverseDnsAsync(string ipAddress, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (!IPAddress.TryParse(ipAddress, out var address)) return null;

        try
        {
            var entry = await Dns.GetHostEntryAsync(address)
                .WaitAsync(timeout ?? TimeSpan.FromSeconds(2), ct)
                .ConfigureAwait(false);

            if (entry.HostName is { Length: > 0 } name && IsUsable(name) && name != ipAddress)
                return name;
        }
        catch (Exception)
        {
            // no PTR record / timeout / cancellation — hostname is simply unknown
        }

        return null;
    }

    private static bool IsUsable(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && value.Trim() is not ("0.0.0.0" or "255.255.255.255")
           && value.Trim().Any(char.IsLetter);
}
