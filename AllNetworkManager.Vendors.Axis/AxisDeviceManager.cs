namespace AllNetworkManager.Vendors.Axis;

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AllNetworkManager.Core.Devices;
using AllNetworkManager.Core.Vendors;
using AllNetworkManager.Core.Exceptions;
using Microsoft.Extensions.Logging;
using CoreLogLevel = AllNetworkManager.Core.Vendors.LogLevel;

/// <summary>
/// Axis vendor layer over VAPIX (HTTP/HTTPS + Digest auth).
/// Backup is a parameter dump; restore is deliberately not offered because a blind param update would need
/// writable-parameter filtering that this app does not do yet.
/// </summary>
public class AxisDeviceManager : IVendorDeviceManager, IVendorCapabilities
{
    private readonly ILogger<AxisDeviceManager> _logger;

    public string VendorName => "Axis";
    public string[] SupportedModels => new[]
    {
        "AXIS ", "M30", "M31", "M32", "M42", "P13", "P14", "P32", "P37", "P38",
        "Q16", "Q35", "Q60", "Q61", "Q63", "F34", "F41", "F44", "F72", "M20", "P12"
    };

    public bool SupportsNetworkConfig => true;
    public bool SupportsFirmwareUpdate => true;
    public bool SupportsConfigBackupRestore => true;
    public bool SupportsConfigRestore => false;
    public bool SupportsUserManagement => true;
    public bool SupportsLogs => true;
    public bool SupportsReboot => true;
    public bool SupportsFactoryReset => true;
    public bool SupportsPtz => false;
    public bool SupportsLiveView => false;
    public bool SupportsRecording => false;
    public bool SupportsAnalytics => false;

    public AxisDeviceManager(ILogger<AxisDeviceManager> logger) => _logger = logger;

    private VapixClient Client(IDevice device, DeviceCredentials credentials)
        => new(_logger, device.IpAddress, device.Port, credentials);

    public async Task<bool> CanManageAsync(IDevice device)
    {
        if (device.Manufacturer.Contains("axis", StringComparison.OrdinalIgnoreCase)) return true;
        if (device.Model.StartsWith("AXIS", StringComparison.OrdinalIgnoreCase)) return true;

        try
        {
            using var client = Client(device, new DeviceCredentials("root", ""));
            await client.GetStringAsync("axis-cgi/basicdeviceinfo.cgi");
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<DeviceInfo> GetDeviceInfoAsync(IDevice device, DeviceCredentials credentials, CancellationToken ct = default)
    {
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var client = Client(device, credentials);

            try
            {
                using var json = await client.GetJsonAsync("axis-cgi/basicdeviceinfo.cgi", ct).ConfigureAwait(false);
                Flatten(json.RootElement, properties);
            }
            catch (VapixException ex)
            {
                _logger.LogDebug("basicdeviceinfo unavailable ({Message}), falling back to param.cgi", ex.Message);
            }

            // param.cgi carries the MAC and the exact firmware build even on old firmware.
            try
            {
                var dump = await client.GetStringAsync(
                    "axis-cgi/param.cgi?action=list&group=Brand,Properties.Firmware,Network,root.Network", ct).ConfigureAwait(false);
                foreach (var kv in VapixClient.ParseParamText(dump)) properties[kv.Key] = kv.Value;
            }
            catch (VapixException ex)
            {
                _logger.LogDebug("param.cgi dump failed: {Message}", ex.Message);
            }
        }
        catch (VapixException ex) when (ex.StatusCode is 401 or 403)
        {
            throw new AuthenticationException(device.Id, VendorName, "VAPIX login failed (401/403) — zkontroluj uživatele a heslo");
        }
        catch (Exception ex) when (ex is not DeviceManagerException)
        {
            throw new DeviceManagerException($"VAPIX device info failed: {ex.Message}", device.Id, VendorName, ErrorCode.ProtocolError, ex);
        }

        string Get(params string[] keys)
        {
            foreach (var key in keys)
            {
                if (properties.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)) return value;
                var match = properties.FirstOrDefault(kv => kv.Key.EndsWith(key, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrEmpty(match.Value)) return match.Value;
            }
            return "";
        }

        // Prefer the short product name from param.cgi; basicdeviceinfo also returns the marketing name
        // ("AXIS P3265-LVE Dome Camera"), which is too long for an inventory column.
        var model = Get("Brand.ProdFullName", "ProdNbr", "ProdFullName", "Brand.ProdNbr");
        var serial = Get("SerialNumber", "Brand.SerialNumber");
        var firmware = Get("Version", "Properties.Firmware.Version");
        var hardware = Get("HardwareID", "Brand.HardwareID", "ProdNbr");
        var mac = Get("Network.eth0.MACAddress", "MACAddress", "Network.MACAddress");

        var extra = properties.ToDictionary(kv => kv.Key, kv => (object)kv.Value);
        extra["hostname"] = Get("Network.HostName", "Network.Hostname", "ServerName", "HostName");

        return new DeviceInfo(
            Manufacturer: "Axis",
            Model: string.IsNullOrWhiteSpace(model) ? device.Model : model,
            SerialNumber: serial,
            FirmwareVersion: firmware,
            HardwareVersion: hardware,
            MacAddress: mac,
            ManufactureDate: null,
            AdditionalInfo: extra);
    }

    private static void Flatten(JsonElement element, Dictionary<string, string> target, int depth = 0)
    {
        if (depth > 6) return;

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    if (prop.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                        target[prop.Name] = prop.Value.ToString();
                    else
                        Flatten(prop.Value, target, depth + 1);
                }
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray()) Flatten(item, target, depth + 1);
                break;

            case JsonValueKind.String:
            case JsonValueKind.Number:
                target["value"] = element.ToString();
                break;
        }
    }

    public async Task<bool> SetNetworkConfigAsync(IDevice device, DeviceCredentials credentials, NetworkConfig config, CancellationToken ct = default)
    {
        try
        {
            using var client = Client(device, credentials);

            var query = new StringBuilder("axis-cgi/param.cgi?action=update");
            if (config.IsDhcp)
            {
                query.Append("&Network.eth0.BootProto=DHCP");
            }
            else
            {
                query.Append("&Network.eth0.BootProto=none");
                query.Append($"&Network.eth0.IPAddress={Uri.EscapeDataString(config.IpAddress)}");
                query.Append($"&Network.eth0.SubnetMask={Uri.EscapeDataString(config.SubnetMask)}");
                if (!string.IsNullOrWhiteSpace(config.Gateway))
                    query.Append($"&Network.eth0.Router={Uri.EscapeDataString(config.Gateway)}");
            }

            var body = await client.GetStringAsync(query.ToString(), ct).ConfigureAwait(false);

            if (body.Contains("Error", StringComparison.OrdinalIgnoreCase) ||
                body.Contains("failed", StringComparison.OrdinalIgnoreCase))
                throw new ConfigurationException(device.Id, VendorName, $"VAPIX odmítl konfiguraci sítě: {body.Trim()}");

            if (!string.IsNullOrWhiteSpace(config.Dns1))
            {
                // Axis exposes DNS as Network.DNS.1/2 on some models only; a rejected param aborts the whole
                // update, so this is attempted separately and its failure is reported but not fatal.
                try
                {
                    var dnsQuery = $"axis-cgi/param.cgi?action=update&Network.DNS.1={Uri.EscapeDataString(config.Dns1)}";
                    if (!string.IsNullOrWhiteSpace(config.Dns2))
                        dnsQuery += $"&Network.DNS.2={Uri.EscapeDataString(config.Dns2)}";

                    await client.GetStringAsync(dnsQuery, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("VAPIX DNS param update failed on {Device}: {Message}", device.IpAddress, ex.Message);
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is not DeviceManagerException)
        {
            throw new ConfigurationException(device.Id, VendorName, $"VAPIX network configuration failed: {ex.Message}", ex);
        }
    }

    public async Task<bool> UpdateFirmwareAsync(IDevice device, DeviceCredentials credentials, FirmwareImage firmware, CancellationToken ct = default)
    {
        using var client = Client(device, credentials);

        var body = await client.UploadFirmwareAsync("axis-cgi/firmwareupgrade.cgi", firmware.FileName, firmware.Data, ct)
            .ConfigureAwait(false);

        _logger.LogInformation("VAPIX firmware upload to {Ip}: {Response}", device.IpAddress, body.Trim());

        if (body.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("failed", StringComparison.OrdinalIgnoreCase))
            throw new FirmwareUpdateException(device.Id, VendorName, $"VAPIX firmware upload failed: {body.Trim()}");

        return true;
    }

    /// <summary>Parameter dump (param.cgi?action=list). Documentation/diff snapshot, not a restorable .bin.</summary>
    public async Task<ConfigurationBackup> BackupConfigurationAsync(IDevice device, DeviceCredentials credentials, CancellationToken ct = default)
    {
        try
        {
            using var client = Client(device, credentials);
            var dump = await client.GetStringAsync("axis-cgi/param.cgi?action=list", ct).ConfigureAwait(false);

            return new ConfigurationBackup(
                DeviceId: device.Id,
                DeviceModel: device.Model,
                FirmwareVersion: device.FirmwareVersion,
                BackupDate: DateTime.UtcNow,
                Data: Encoding.UTF8.GetBytes(dump),
                Format: "axis-param-dump");
        }
        catch (Exception ex) when (ex is not DeviceManagerException)
        {
            throw new ConfigurationException(device.Id, VendorName, $"VAPIX backup failed: {ex.Message}", ex);
        }
    }

    public Task<bool> RestoreConfigurationAsync(IDevice device, DeviceCredentials credentials, ConfigurationBackup backup, CancellationToken ct = default)
        => throw new DeviceManagerException(
            "Obnova parametrů není implementována — vyžadovala by filtrování zapisovatelných parametrů.",
            device.Id, VendorName, ErrorCode.UnsupportedOperation);

    public async Task<IEnumerable<UserAccount>> GetUsersAsync(IDevice device, DeviceCredentials credentials, CancellationToken ct = default)
    {
        using var client = Client(device, credentials);
        var users = new List<UserAccount>();

        try
        {
            var body = await client.PostJsonAsync("axis-cgi/usergroup.cgi",
                "{\"apiVersion\":\"1.0\",\"method\":\"get\"}", ct).ConfigureAwait(false);

            using var doc = JsonDocument.Parse(body);
            foreach (var entry in FindUserObjects(doc.RootElement))
            {
                var name = ReadString(entry, "name", "user", "username");
                if (string.IsNullOrWhiteSpace(name)) continue;

                users.Add(new UserAccount(
                    Username: name,
                    Password: null,
                    Role: MapRole(ReadString(entry, "grp", "group", "role")),
                    IsEnabled: !string.Equals(ReadString(entry, "enabled", "active"), "false", StringComparison.OrdinalIgnoreCase),
                    Email: null,
                    LastLogin: null,
                    Permissions: new Dictionary<string, object> { ["grp"] = ReadString(entry, "grp", "group", "role") }));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("usergroup.cgi failed ({Message}), trying legacy pwdgrp.cgi", ex.Message);

            var text = await client.GetStringAsync("axis-cgi/pwdgrp.cgi?action=list", ct).ConfigureAwait(false);
            foreach (var line in text.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0) continue;

                var parts = trimmed.Split(':', '=');
                if (parts.Length < 2) continue;

                users.Add(new UserAccount(
                    Username: parts[1].Trim(),
                    Password: null,
                    Role: MapRole(parts[0]),
                    IsEnabled: true,
                    Email: null,
                    LastLogin: null,
                    Permissions: new Dictionary<string, object> { ["grp"] = parts[0].Trim() }));
            }
        }

        return users;
    }

    public async Task<bool> SetUserAsync(IDevice device, DeviceCredentials credentials, UserAccount user, CancellationToken ct = default)
    {
        using var client = Client(device, credentials);

        var existing = (await GetUsersAsync(device, credentials, ct).ConfigureAwait(false))
            .Any(u => u.Username.Equals(user.Username, StringComparison.OrdinalIgnoreCase));

        var method = existing ? "update" : "add";
        var payload = JsonSerializer.Serialize(new
        {
            apiVersion = "1.0",
            method,
            @params = new
            {
                user = user.Username,
                pwd = user.Password ?? "",
                grp = MapGroup(user.Role)
            }
        });

        var body = await client.PostJsonAsync("axis-cgi/usergroup.cgi", payload, ct).ConfigureAwait(false);
        if (body.Contains("\"error\"", StringComparison.OrdinalIgnoreCase))
            throw new DeviceManagerException($"VAPIX uživatele neuložil: {body}", device.Id, VendorName, ErrorCode.UserManagementFailed);

        return true;
    }

    public async Task<bool> DeleteUserAsync(IDevice device, DeviceCredentials credentials, string username, CancellationToken ct = default)
    {
        using var client = Client(device, credentials);

        var payload = JsonSerializer.Serialize(new
        {
            apiVersion = "1.0",
            method = "delete",
            @params = new { user = username }
        });

        var body = await client.PostJsonAsync("axis-cgi/usergroup.cgi", payload, ct).ConfigureAwait(false);
        return !body.Contains("\"error\"", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<DeviceLog[]> GetLogsAsync(IDevice device, DeviceCredentials credentials, LogFilter? filter = null, CancellationToken ct = default)
    {
        using var client = Client(device, credentials);
        var text = await client.GetStringAsync("axis-cgi/admin/systemlog.cgi", ct).ConfigureAwait(false);

        var logs = new List<DeviceLog>();
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;

            var parsed = ParseLogLine(trimmed);
            if (parsed == null) continue;

            logs.Add(parsed);
        }

        if (filter?.MinLevel != null)
            logs = logs.Where(l => l.Level >= filter.MinLevel).ToList();

        if (!string.IsNullOrWhiteSpace(filter?.SearchText))
            logs = logs.Where(l => l.Message.Contains(filter.SearchText, StringComparison.OrdinalIgnoreCase)).ToList();

        return logs.Take(filter?.MaxCount ?? 1000).ToArray();
    }

    private static readonly Regex LogPattern = new(
        @"^(?<time>\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}:\d{2}(?:\.\d+)?)\s*\[\s*(?<level>[A-Za-z]+)\s*\]\s*(?:(?<source>[\w.-]+)\s*-\s*)?(?<message>.*)$",
        RegexOptions.Compiled);

    private static DeviceLog? ParseLogLine(string line)
    {
        var match = LogPattern.Match(line);
        if (match.Success)
        {
            var level = MapLevel(match.Groups["level"].Value);
            var timestamp = DateTime.TryParse(match.Groups["time"].Value, out var parsed) ? parsed : DateTime.UtcNow;

            return new DeviceLog(
                Timestamp: timestamp,
                Level: level,
                Source: match.Groups["source"].Success ? match.Groups["source"].Value : "",
                Message: match.Groups["message"].Value.Trim(),
                Metadata: null);
        }

        // Lines from old firmware have no timestamp/level prefix at all.
        return new DeviceLog(DateTime.UtcNow, CoreLogLevel.Information, "", line, null);
    }

    public async Task RebootAsync(IDevice device, DeviceCredentials credentials, CancellationToken ct = default)
    {
        using var client = Client(device, credentials);
        await client.GetStringAsync("axis-cgi/admin/restart.cgi", ct).ConfigureAwait(false);
    }

    public async Task FactoryResetAsync(IDevice device, DeviceCredentials credentials, CancellationToken ct = default)
    {
        using var client = Client(device, credentials);
        await client.GetStringAsync("axis-cgi/factorydefault.cgi", ct).ConfigureAwait(false);
    }

    private static IEnumerable<JsonElement> FindUserObjects(JsonElement element, int depth = 0)
    {
        if (depth > 6) yield break;

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object) yield return item;
                else foreach (var nested in FindUserObjects(item, depth + 1)) yield return nested;
            }
            yield break;
        }

        if (element.ValueKind != JsonValueKind.Object) yield break;

        foreach (var prop in element.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.Array &&
                prop.Name.Contains("user", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var item in prop.Value.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.Object) yield return item;
            }
            else
            {
                foreach (var nested in FindUserObjects(prop.Value, depth + 1)) yield return nested;
            }
        }
    }

    private static string ReadString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString() ?? "";
        }
        return "";
    }

    private static UserRole MapRole(string group) => group.ToLowerInvariant() switch
    {
        "admin" or "administrator" => UserRole.Admin,
        "operator" => UserRole.Operator,
        "viewer" or "ptz" => UserRole.Viewer,
        _ => UserRole.Custom
    };

    private static string MapGroup(UserRole role) => role switch
    {
        UserRole.Admin => "admin",
        UserRole.Operator => "operator",
        _ => "viewer"
    };

    private static CoreLogLevel MapLevel(string level) => level.ToLowerInvariant() switch
    {
        "error" or "err" or "crit" or "critical" or "fatal" => CoreLogLevel.Error,
        "warn" or "warning" => CoreLogLevel.Warning,
        "debug" or "trace" => CoreLogLevel.Debug,
        _ => CoreLogLevel.Information
    };
}
