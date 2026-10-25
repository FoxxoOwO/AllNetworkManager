namespace AllNetworkManager.Vendors.MikroTik;

using System.Text;
using System.Text.Json;
using AllNetworkManager.Core.Devices;
using AllNetworkManager.Core.Vendors;
using AllNetworkManager.Core.Exceptions;
using Microsoft.Extensions.Logging;
using CoreLogLevel = AllNetworkManager.Core.Vendors.LogLevel;

/// <summary>
/// MikroTik RouterOS vendor layer over the binary API (8728 plain / 8729 TLS).
/// Firmware and config restore are not implemented over the API — capability flags say so instead of
/// offering buttons that cannot work.
/// </summary>
public class MikroTikDeviceManager : IVendorDeviceManager, IVendorCapabilities
{
    private readonly ILogger<MikroTikDeviceManager> _logger;

    public string VendorName => "MikroTik";
    public string[] SupportedModels => new[]
    {
        "RouterBOARD", "hEX", "hAP", "CCR", "CRS", "RB", "CHR", "Cloud Core", "Cloud Router"
    };

    public bool SupportsNetworkConfig => true;
    public bool SupportsFirmwareUpdate => false;
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

    public MikroTikDeviceManager(ILogger<MikroTikDeviceManager> logger) => _logger = logger;

    private async Task<RouterOsApiClient> ConnectAsync(IDevice device, DeviceCredentials credentials, CancellationToken ct)
    {
        var port = device.Port is > 0 ? device.Port!.Value : 8728;
        var client = new RouterOsApiClient(_logger, device.IpAddress, port);

        try
        {
            await client.ConnectAsync(useTls: port == 8729, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            await client.DisposeAsync().ConfigureAwait(false);
            // unreachable device, wrong port or TLS mismatch is not an authentication problem
            throw new DeviceUnreachableException(device.Id, VendorName, $"RouterOS {device.IpAddress}:{port} unreachable: {ex.Message}");
        }

        try
        {
            await client.LoginAsync(credentials.Username, credentials.Password, ct).ConfigureAwait(false);
            return client;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw new AuthenticationException(device.Id, VendorName, $"RouterOS login failed: {ex.Message}");
        }
    }

    public async Task<bool> CanManageAsync(IDevice device)
    {
        if (string.Equals(device.Manufacturer, "MikroTik", StringComparison.OrdinalIgnoreCase)) return true;
        if (device.Properties.ContainsKey("mndp_version")) return true;

        try
        {
            await using var client = new RouterOsApiClient(_logger, device.IpAddress, 8728);
            await client.ConnectAsync(false);
            return client.IsConnected;
        }
        catch
        {
            return false;
        }
    }

    public async Task<DeviceInfo> GetDeviceInfoAsync(IDevice device, DeviceCredentials credentials, CancellationToken ct = default)
    {
        try
        {
            await using var api = await ConnectAsync(device, credentials, ct).ConfigureAwait(false);

            var resource = First(await api.SendAsync(new[] { "/system/resource/print" }, ct).ConfigureAwait(false));
            var identity = First(await api.SendAsync(new[] { "/system/identity/print" }, ct).ConfigureAwait(false));

            var routerboard = new Dictionary<string, string>();
            try
            {
                routerboard = First(await api.SendAsync(new[] { "/system/routerboard/print" }, ct).ConfigureAwait(false));
            }
            catch (RouterOsException)
            {
                // CHR / x86 installs have no routerboard section
            }

            var model = routerboard.GetValueOrDefault("model")
                        ?? resource.GetValueOrDefault("board-name")
                        ?? "RouterOS";

            var extra = new Dictionary<string, object>
            {
                ["identity"] = identity.GetValueOrDefault("name", ""),
                ["uptime"] = resource.GetValueOrDefault("uptime", ""),
                ["platform"] = resource.GetValueOrDefault("platform", ""),
                ["architecture"] = resource.GetValueOrDefault("architecture-name", ""),
                ["cpu"] = resource.GetValueOrDefault("cpu", ""),
                ["cpuCount"] = resource.GetValueOrDefault("cpu-count", ""),
                ["freeMemory"] = resource.GetValueOrDefault("free-memory", ""),
                ["totalMemory"] = resource.GetValueOrDefault("total-memory", ""),
                ["routerOsVersion"] = resource.GetValueOrDefault("version", ""),
                ["buildTime"] = resource.GetValueOrDefault("build-time", ""),
                ["currentFirmware"] = routerboard.GetValueOrDefault("current-firmware", ""),
                ["upgradeFirmware"] = routerboard.GetValueOrDefault("upgrade-firmware", "")
            };

            return new DeviceInfo(
                Manufacturer: "MikroTik",
                Model: model,
                SerialNumber: routerboard.GetValueOrDefault("serial-number", ""),
                FirmwareVersion: resource.GetValueOrDefault("version", ""),
                HardwareVersion: routerboard.GetValueOrDefault("current-firmware", ""),
                MacAddress: "",
                ManufactureDate: null,
                AdditionalInfo: extra);
        }
        catch (Exception ex) when (ex is not DeviceManagerException)
        {
            throw new DeviceManagerException($"RouterOS device info failed: {ex.Message}", device.Id, VendorName, ErrorCode.ProtocolError, ex);
        }
    }

    public async Task<bool> SetNetworkConfigAsync(IDevice device, DeviceCredentials credentials, NetworkConfig config, CancellationToken ct = default)
    {
        try
        {
            await using var api = await ConnectAsync(device, credentials, ct).ConfigureAwait(false);
            var addresses = await api.SendAsync(new[] { "/ip/address/print" }, ct).ConfigureAwait(false);

            var target = addresses.FirstOrDefault(a =>
                             !a.GetValueOrDefault("dynamic", "false").Equals("true", StringComparison.OrdinalIgnoreCase) &&
                             (a.GetValueOrDefault("address", "").StartsWith(device.IpAddress + "/", StringComparison.Ordinal)))
                         ?? addresses.FirstOrDefault(a => !a.GetValueOrDefault("dynamic", "false").Equals("true", StringComparison.OrdinalIgnoreCase));

            if (config.IsDhcp)
            {
                var interfaceName = target?.GetValueOrDefault("interface") ?? "ether1";
                var clients = await api.SendAsync(new[] { "/ip/dhcp-client/print" }, ct).ConfigureAwait(false);
                var client = clients.FirstOrDefault(c =>
                    c.GetValueOrDefault("interface", "").Equals(interfaceName, StringComparison.OrdinalIgnoreCase));

                if (client != null && client.GetValueOrDefault("disabled", "false").Equals("true", StringComparison.OrdinalIgnoreCase))
                    await api.SendAsync(new[] { "/ip/dhcp-client/enable", $"=numbers={client.GetValueOrDefault("id", ".id")}" }, ct).ConfigureAwait(false);
                else if (client == null)
                    await api.SendAsync(new[] { "/ip/dhcp-client/add", $"=interface={interfaceName}" }, ct).ConfigureAwait(false);

                return true;
            }

            if (target == null)
                throw new ConfigurationException(device.Id, VendorName, "Device has no static IPv4 address to edit");

            var id = target.GetValueOrDefault("id", target.GetValueOrDefault(".id", ""));
            await api.SendAsync(new[]
            {
                "/ip/address/set",
                $"=numbers={id}",
                $"=address={config.IpAddress}/{PrefixLength(config.SubnetMask)}"
            }, ct).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(config.Gateway))
            {
                var routes = await api.SendAsync(new[] { "/ip/route/print" }, ct).ConfigureAwait(false);
                var defaultRoute = routes.FirstOrDefault(r => r.GetValueOrDefault("dst-address", "") == "0.0.0.0/0");

                if (defaultRoute != null)
                {
                    var routeId = defaultRoute.GetValueOrDefault("id", defaultRoute.GetValueOrDefault(".id", ""));
                    await api.SendAsync(new[] { "/ip/route/set", $"=numbers={routeId}", $"=gateway={config.Gateway}" }, ct).ConfigureAwait(false);
                }
                else
                {
                    await api.SendAsync(new[]
                    {
                        "/ip/route/add",
                        "=dst-address=0.0.0.0/0",
                        $"=gateway={config.Gateway}",
                        $"=comment=AllNetworkManager"
                    }, ct).ConfigureAwait(false);
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is not DeviceManagerException)
        {
            throw new ConfigurationException(device.Id, VendorName, $"RouterOS network configuration failed: {ex.Message}", ex);
        }
    }

    public Task<bool> UpdateFirmwareAsync(IDevice device, DeviceCredentials credentials, FirmwareImage firmware, CancellationToken ct = default)
        => throw new DeviceManagerException(
            "RouterOS firmware is installed from a .npk package or via /system/package/update, not uploaded over the API.",
            device.Id, VendorName, ErrorCode.UnsupportedOperation);

    /// <summary>Snapshot of config sections as JSON. Not a RouterOS .backup file and not restorable by this app.</summary>
    public async Task<ConfigurationBackup> BackupConfigurationAsync(IDevice device, DeviceCredentials credentials, CancellationToken ct = default)
    {
        var sections = new[]
        {
            "/system/identity/print",
            "/interface/print",
            "/ip/address/print",
            "/ip/route/print",
            "/ip/dhcp-client/print",
            "/ip/dhcp-server/print",
            "/ip/dhcp-server/lease/print",
            "/ip/firewall/filter/print",
            "/ip/firewall/nat/print",
            "/ip/service/print",
            "/user/print",
            "/system/resource/print"
        };

        try
        {
            await using var api = await ConnectAsync(device, credentials, ct).ConfigureAwait(false);
            var snapshot = new Dictionary<string, object>();

            foreach (var section in sections)
            {
                try
                {
                    snapshot[section] = await api.SendAsync(new[] { section }, ct).ConfigureAwait(false);
                }
                catch (RouterOsException ex)
                {
                    snapshot[section] = new { error = ex.Message };
                }
            }

            var json = JsonSerializer.Serialize(new
            {
                device = device.IpAddress,
                takenAt = DateTime.UtcNow.ToString("O"),
                model = device.Model,
                sections = snapshot
            }, new JsonSerializerOptions { WriteIndented = true });

            return new ConfigurationBackup(
                DeviceId: device.Id,
                DeviceModel: device.Model,
                FirmwareVersion: device.FirmwareVersion,
                BackupDate: DateTime.UtcNow,
                Data: Encoding.UTF8.GetBytes(json),
                Format: "routeros-json-snapshot");
        }
        catch (Exception ex) when (ex is not DeviceManagerException)
        {
            throw new ConfigurationException(device.Id, VendorName, $"RouterOS backup failed: {ex.Message}", ex);
        }
    }

    public Task<bool> RestoreConfigurationAsync(IDevice device, DeviceCredentials credentials, ConfigurationBackup backup, CancellationToken ct = default)
        => throw new DeviceManagerException(
            "Restoring a RouterOS snapshot is not implemented; upload a .backup/.rsc file through WinBox/SSH.",
            device.Id, VendorName, ErrorCode.UnsupportedOperation);

    public async Task<IEnumerable<UserAccount>> GetUsersAsync(IDevice device, DeviceCredentials credentials, CancellationToken ct = default)
    {
        await using var api = await ConnectAsync(device, credentials, ct).ConfigureAwait(false);
        var rows = await api.SendAsync(new[] { "/user/print" }, ct).ConfigureAwait(false);

        return rows.Select(r => new UserAccount(
            Username: r.GetValueOrDefault("name", ""),
            Password: null,
            Role: MapRoleFromGroup(r.GetValueOrDefault("group", "")),
            IsEnabled: !r.GetValueOrDefault("disabled", "false").Equals("true", StringComparison.OrdinalIgnoreCase),
            Email: null,
            LastLogin: null,
            Permissions: new Dictionary<string, object> { ["group"] = r.GetValueOrDefault("group", "") })).ToList();
    }

    public async Task<bool> SetUserAsync(IDevice device, DeviceCredentials credentials, UserAccount user, CancellationToken ct = default)
    {
        await using var api = await ConnectAsync(device, credentials, ct).ConfigureAwait(false);
        var group = MapGroupFromRole(user.Role);
        var rows = await api.SendAsync(new[] { "/user/print" }, ct).ConfigureAwait(false);
        var existing = rows.FirstOrDefault(r =>
            r.GetValueOrDefault("name", "").Equals(user.Username, StringComparison.OrdinalIgnoreCase));

        if (existing == null)
        {
            var words = new List<string> { "/user/add", $"=name={user.Username}", $"=group={group}" };
            if (!string.IsNullOrEmpty(user.Password)) words.Add($"=password={user.Password}");
            await api.SendAsync(words.ToArray(), ct).ConfigureAwait(false);
        }
        else
        {
            var id = existing.GetValueOrDefault("id", existing.GetValueOrDefault(".id", ""));
            var words = new List<string> { "/user/set", $"=numbers={id}", $"=group={group}" };
            if (!string.IsNullOrEmpty(user.Password)) words.Add($"=password={user.Password}");
            if (user.IsEnabled) words.Add("=disabled=no"); else words.Add("=disabled=yes");
            await api.SendAsync(words.ToArray(), ct).ConfigureAwait(false);
        }

        return true;
    }

    public async Task<bool> DeleteUserAsync(IDevice device, DeviceCredentials credentials, string username, CancellationToken ct = default)
    {
        await using var api = await ConnectAsync(device, credentials, ct).ConfigureAwait(false);
        var rows = await api.SendAsync(new[] { "/user/print" }, ct).ConfigureAwait(false);
        var target = rows.FirstOrDefault(r => r.GetValueOrDefault("name", "").Equals(username, StringComparison.OrdinalIgnoreCase));
        if (target == null) return false;

        var id = target.GetValueOrDefault("id", target.GetValueOrDefault(".id", ""));
        await api.SendAsync(new[] { "/user/remove", $"=numbers={id}" }, ct).ConfigureAwait(false);
        return true;
    }

    public async Task<DeviceLog[]> GetLogsAsync(IDevice device, DeviceCredentials credentials, LogFilter? filter = null, CancellationToken ct = default)
    {
        await using var api = await ConnectAsync(device, credentials, ct).ConfigureAwait(false);
        var rows = await api.SendAsync(new[] { "/log/print" }, ct).ConfigureAwait(false);

        var logs = rows.Select(r =>
        {
            var topics = r.GetValueOrDefault("topics", "");
            return new DeviceLog(
                Timestamp: DateTime.UtcNow,
                Level: LevelFromTopics(topics),
                Source: topics,
                Message: r.GetValueOrDefault("message", ""),
                Metadata: new Dictionary<string, object> { ["routerOsTime"] = r.GetValueOrDefault("time", "") });
        }).ToList();

        if (filter?.MinLevel != null)
            logs = logs.Where(l => l.Level >= filter.MinLevel).ToList();

        if (!string.IsNullOrWhiteSpace(filter?.SearchText))
            logs = logs.Where(l => l.Message.Contains(filter.SearchText, StringComparison.OrdinalIgnoreCase)).ToList();

        return logs.Take(filter?.MaxCount ?? 1000).ToArray();
    }

    public async Task RebootAsync(IDevice device, DeviceCredentials credentials, CancellationToken ct = default)
    {
        await using var api = await ConnectAsync(device, credentials, ct).ConfigureAwait(false);
        await api.SendAsync(new[] { "/system/reboot" }, ct).ConfigureAwait(false);
    }

    public async Task FactoryResetAsync(IDevice device, DeviceCredentials credentials, CancellationToken ct = default)
    {
        await using var api = await ConnectAsync(device, credentials, ct).ConfigureAwait(false);
        await api.SendAsync(new[] { "/system/reset-configuration" }, ct).ConfigureAwait(false);
    }

    private static Dictionary<string, string> First(List<Dictionary<string, string>> rows)
        => rows.FirstOrDefault() ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private static string PrefixLength(string subnetMask)
    {
        if (!System.Net.IPAddress.TryParse(subnetMask, out var ip)) return "24";
        var bytes = ip.GetAddressBytes();
        int cidr = 0;
        foreach (var b in bytes)
            for (int i = 7; i >= 0; i--)
                if ((b & (1 << i)) != 0) cidr++; else return cidr.ToString();
        return cidr.ToString();
    }

    private static UserRole MapRoleFromGroup(string group) => group.ToLowerInvariant() switch
    {
        "full" => UserRole.Admin,
        "write" => UserRole.Operator,
        "read" => UserRole.Viewer,
        _ => UserRole.Custom
    };

    private static string MapGroupFromRole(UserRole role) => role switch
    {
        UserRole.Admin => "full",
        UserRole.Operator => "write",
        UserRole.Viewer => "read",
        _ => "read"
    };

    private static CoreLogLevel LevelFromTopics(string topics)
    {
        if (topics.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            topics.Contains("critical", StringComparison.OrdinalIgnoreCase)) return CoreLogLevel.Error;
        if (topics.Contains("warning", StringComparison.OrdinalIgnoreCase)) return CoreLogLevel.Warning;
        if (topics.Contains("debug", StringComparison.OrdinalIgnoreCase)) return CoreLogLevel.Debug;
        return CoreLogLevel.Information;
    }
}
