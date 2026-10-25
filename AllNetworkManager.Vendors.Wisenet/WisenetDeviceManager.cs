namespace AllNetworkManager.Vendors.Wisenet;

using System.Xml.Linq;
using AllNetworkManager.Core.Devices;
using AllNetworkManager.Core.Vendors;
using AllNetworkManager.Core.Exceptions;
using Microsoft.Extensions.Logging;
using CoreLogLevel = AllNetworkManager.Core.Vendors.LogLevel;

/// <summary>
/// Hanwha/Samsung Wisenet vendor layer over SUNAPI (HTTP CGI, Digest auth).
/// Endpoint shapes follow the SUNAPI reference; parsing is deliberately tolerant because the schema
/// differs between firmware generations and model lines. Firmware upload is implemented but disabled by
/// capability flag until it is confirmed against real hardware.
/// </summary>
public class WisenetDeviceManager : IVendorDeviceManager, IVendorCapabilities
{
    private readonly ILogger<WisenetDeviceManager> _logger;

    public string VendorName => "Wisenet";
    public string[] SupportedModels => new[]
    {
        "XND", "XNV", "XNP", "XNB", "XNZ",
        "QND", "QNV", "QNP", "QNB",
        "TND", "TNV", "TNP",
        "PND", "PNV", "PNP", "PNB",
        "HRX", "HRD", "SRN", "WRN", "PRN"
    };

    public bool SupportsNetworkConfig => true;
    public bool SupportsFirmwareUpdate => false;
    public bool SupportsConfigBackupRestore => false;
    public bool SupportsConfigRestore => false;
    public bool SupportsUserManagement => true;
    public bool SupportsLogs => true;
    public bool SupportsReboot => true;
    public bool SupportsFactoryReset => true;
    public bool SupportsPtz => true;
    public bool SupportsLiveView => true;
    public bool SupportsRecording => false;
    public bool SupportsAnalytics => true;

    public WisenetDeviceManager(ILogger<WisenetDeviceManager> logger) => _logger = logger;

    private SunapiClient Client(IDevice device, DeviceCredentials credentials)
        => new(_logger, device.IpAddress, device.Port == 80 ? null : device.Port, credentials);

    private static string Root(IDevice device)
    {
        var scheme = device.Port == 443 ? "https" : "http";
        var port = device.Port is 80 or null or 443 ? "" : ":" + device.Port;
        return $"{scheme}://{device.IpAddress}{port}";
    }

    public async Task<bool> CanManageAsync(IDevice device)
    {
        var text = $"{device.Manufacturer} {device.Model}";
        if (text.Contains("wisenet", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("hanwha", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("samsung", StringComparison.OrdinalIgnoreCase))
            return true;

        if (SupportedModels.Any(m => device.Model.StartsWith(m, StringComparison.OrdinalIgnoreCase)))
            return true;

        try
        {
            using var client = Client(device, new DeviceCredentials("", ""));
            var doc = await client.ViewAsync("system", "version");
            return doc.Root != null;
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
            using var client = Client(device, credentials);

            var version = await client.ViewAsync("system", "version", null, ct).ConfigureAwait(false);
            var info = await TryViewAsync(client, "system", "systeminfo", ct);
            var caps = await TryViewAsync(client, "system", "capability", ct);

            var model = Xml.FindValue(info, "Model", "DeviceModel", "ModelName")
                        ?? Xml.FindValue(version, "Model", "ModelName")
                        ?? device.Model;

            var serial = Xml.FindValue(info, "SerialNumber", "SerialNo", "Serial")
                         ?? Xml.FindValue(version, "SerialNumber", "SerialNo")
                         ?? "";

            var firmware = Xml.FindValue(version, "Version", "FirmwareVersion", "SWVersion", "SoftwareVersion")
                           ?? Xml.FindValue(info, "FirmwareVersion", "SWVersion")
                           ?? device.FirmwareVersion ?? "";

            var hardware = Xml.FindValue(info, "HardwareVersion", "HWVersion", "Hardware")
                           ?? "";

            var extra = new Dictionary<string, object>
            {
                ["firmwareVersion"] = firmware,
                ["systemInfo"] = info.ToString(SaveOptions.DisableFormatting),
                ["capabilitiesRaw"] = caps.ToString(SaveOptions.DisableFormatting),
                ["hostname"] = Xml.FindValue(info, "HostName", "Hostname", "NetworkHostName", "DeviceName") ?? "",
                ["api"] = "SUNAPI"
            };

            return new DeviceInfo(
                Manufacturer: "Hanwha (Wisenet)",
                Model: model,
                SerialNumber: serial,
                FirmwareVersion: firmware,
                HardwareVersion: hardware,
                MacAddress: Xml.FindValue(info, "MacAddress", "MAC") ?? "",
                ManufactureDate: null,
                AdditionalInfo: extra);
        }
        catch (SunapiException ex) when (ex.StatusCode is 401 or 403)
        {
            throw new AuthenticationException(device.Id, VendorName, "SUNAPI login failed (401/403)");
        }
        catch (Exception ex) when (ex is not DeviceManagerException)
        {
            throw new DeviceManagerException($"SUNAPI device info failed: {ex.Message}", device.Id, VendorName, ErrorCode.ProtocolError, ex);
        }
    }

    private async Task<XDocument> TryViewAsync(SunapiClient client, string service, string menu, CancellationToken ct)
    {
        try
        {
            return await client.ViewAsync(service, menu, null, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("SUNAPI {Service}.cgi/{Menu} unavailable: {Message}", service, menu, ex.Message);
            return new XDocument(new XElement("empty"));
        }
    }

    public async Task<bool> SetNetworkConfigAsync(IDevice device, DeviceCredentials credentials, NetworkConfig config, CancellationToken ct = default)
    {
        try
        {
            using var client = Client(device, credentials);

            var parameters = new Dictionary<string, string>
            {
                ["IPAddress"] = config.IpAddress,
                ["SubnetMask"] = config.SubnetMask,
                ["Gateway"] = config.Gateway,
                ["DNS1"] = config.Dns1 ?? "",
                ["DNS2"] = config.Dns2 ?? "",
                ["DHCP"] = config.IsDhcp ? "Enable" : "Disable"
            };

            var status = await client.SetAsync("network", "ipconfig", parameters, ct).ConfigureAwait(false);
            if (!status.Success)
                throw new ConfigurationException(device.Id, VendorName, $"SUNAPI rejected IP config: {status.Message} (code {status.Code})");

            return true;
        }
        catch (Exception ex) when (ex is not DeviceManagerException)
        {
            throw new ConfigurationException(device.Id, VendorName, $"SUNAPI network configuration failed: {ex.Message}", ex);
        }
    }

    public async Task<bool> UpdateFirmwareAsync(IDevice device, DeviceCredentials credentials, FirmwareImage firmware, CancellationToken ct = default)
    {
        using var client = Client(device, credentials);

        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(firmware.Data), "file", firmware.FileName);

        var status = await client.UploadAsync($"{Root(device)}/stw-cgi/upgrade.cgi?msubmenu=update", content, ct)
            .ConfigureAwait(false);

        _logger.LogInformation("SUNAPI firmware upload to {Ip}: success={Success} code={Code}", device.IpAddress, status.Success, status.Code);

        if (!status.Success)
            throw new FirmwareUpdateException(device.Id, VendorName, $"SUNAPI firmware upload failed: {status.Message} (code {status.Code})");

        return true;
    }

    public Task<ConfigurationBackup> BackupConfigurationAsync(IDevice device, DeviceCredentials credentials, CancellationToken ct = default)
        => throw new DeviceManagerException(
            "Wisenet configuration backup over SUNAPI is not verified; use Wisenet Device Manager for .cfg backups.",
            device.Id, VendorName, ErrorCode.UnsupportedOperation);

    public Task<bool> RestoreConfigurationAsync(IDevice device, DeviceCredentials credentials, ConfigurationBackup backup, CancellationToken ct = default)
        => throw new DeviceManagerException(
            "Wisenet configuration restore over SUNAPI is not implemented.",
            device.Id, VendorName, ErrorCode.UnsupportedOperation);

    public async Task<IEnumerable<UserAccount>> GetUsersAsync(IDevice device, DeviceCredentials credentials, CancellationToken ct = default)
    {
        using var client = Client(device, credentials);
        var doc = await client.ViewAsync("websetting", "users", null, ct).ConfigureAwait(false);

        var blocks = Xml.Blocks(doc, "User", "UserInfo", "users").ToList();
        var result = new List<UserAccount>();

        foreach (var block in blocks)
        {
            var name = Xml.FindValue(new XDocument(block), "UserName", "Name", "ID", "User");
            if (string.IsNullOrWhiteSpace(name)) continue;

            var authority = Xml.FindValue(new XDocument(block), "Authority", "UserLevel", "Level", "Group") ?? "";
            var enabledText = Xml.FindValue(new XDocument(block), "Enabled", "Use", "Active");

            result.Add(new UserAccount(
                Username: name,
                Password: null,
                Role: MapRoleFromAuthority(authority),
                IsEnabled: !string.Equals(enabledText, "false", StringComparison.OrdinalIgnoreCase)
                           && !string.Equals(enabledText, "0", StringComparison.Ordinal),
                Email: null,
                LastLogin: null,
                Permissions: new Dictionary<string, object> { ["authority"] = authority }));
        }

        return result;
    }

    public async Task<bool> SetUserAsync(IDevice device, DeviceCredentials credentials, UserAccount user, CancellationToken ct = default)
    {
        using var client = Client(device, credentials);

        var parameters = new Dictionary<string, string>
        {
            ["UserName"] = user.Username,
            ["Authority"] = MapAuthorityFromRole(user.Role),
            ["Enabled"] = user.IsEnabled ? "true" : "false"
        };
        if (!string.IsNullOrEmpty(user.Password)) parameters["Password"] = user.Password;

        var existing = (await GetUsersAsync(device, credentials, ct).ConfigureAwait(false))
            .Any(u => u.Username.Equals(user.Username, StringComparison.OrdinalIgnoreCase));

        var status = existing
            ? await client.SetAsync("websetting", "users", parameters, ct).ConfigureAwait(false)
            : await client.AddAsync("websetting", "users", parameters, ct).ConfigureAwait(false);

        if (!status.Success)
            throw new DeviceManagerException($"SUNAPI user write failed: {status.Message} (code {status.Code})",
                device.Id, VendorName, ErrorCode.UserManagementFailed);

        return true;
    }

    public async Task<bool> DeleteUserAsync(IDevice device, DeviceCredentials credentials, string username, CancellationToken ct = default)
    {
        using var client = Client(device, credentials);
        var status = await client.RemoveAsync("websetting", "users",
            new Dictionary<string, string> { ["UserName"] = username }, ct).ConfigureAwait(false);

        return status.Success;
    }

    public async Task<DeviceLog[]> GetLogsAsync(IDevice device, DeviceCredentials credentials, LogFilter? filter = null, CancellationToken ct = default)
    {
        using var client = Client(device, credentials);

        try
        {
            var doc = await client.ViewAsync("system", "log", null, ct).ConfigureAwait(false);
            var entries = Xml.Blocks(doc, "Log", "LogInfo", "log").ToList();

            var logs = new List<DeviceLog>();
            foreach (var entry in entries)
            {
                var message = Xml.FindValue(new XDocument(entry), "Message", "Description", "Text", "Log") ?? "";
                if (string.IsNullOrWhiteSpace(message)) continue;

                var timeText = Xml.FindValue(new XDocument(entry), "Time", "DateTime", "Date");
                var typeText = Xml.FindValue(new XDocument(entry), "Type", "Level", "Severity") ?? "";

                logs.Add(new DeviceLog(
                    Timestamp: DateTime.TryParse(timeText, out var ts) ? ts : DateTime.UtcNow,
                    Level: MapLevel(typeText),
                    Source: typeText,
                    Message: message,
                    Metadata: new Dictionary<string, object> { ["rawTime"] = timeText ?? "" }));
            }

            if (!string.IsNullOrWhiteSpace(filter?.SearchText))
                logs = logs.Where(l => l.Message.Contains(filter.SearchText, StringComparison.OrdinalIgnoreCase)).ToList();

            return logs.Take(filter?.MaxCount ?? 1000).ToArray();
        }
        catch (SunapiException ex)
        {
            throw new DeviceManagerException($"SUNAPI log read failed: {ex.Message}", device.Id, VendorName, ErrorCode.ProtocolError, ex);
        }
    }

    public async Task RebootAsync(IDevice device, DeviceCredentials credentials, CancellationToken ct = default)
    {
        using var client = Client(device, credentials);
        var status = await client.ControlAsync("system", "reboot", null, ct).ConfigureAwait(false);
        if (!status.Success)
            throw new DeviceManagerException($"SUNAPI reboot rejected: {status.Message} (code {status.Code})",
                device.Id, VendorName, ErrorCode.ProtocolError);
    }

    public async Task FactoryResetAsync(IDevice device, DeviceCredentials credentials, CancellationToken ct = default)
    {
        using var client = Client(device, credentials);
        var status = await client.ControlAsync("system", "factorydefault", null, ct).ConfigureAwait(false);
        if (!status.Success)
            throw new DeviceManagerException($"SUNAPI factory reset rejected: {status.Message} (code {status.Code})",
                device.Id, VendorName, ErrorCode.ProtocolError);
    }

    private static UserRole MapRoleFromAuthority(string authority) => authority.ToLowerInvariant() switch
    {
        "admin" or "administrator" => UserRole.Admin,
        "operator" or "user" => UserRole.Operator,
        "viewer" or "guest" => UserRole.Viewer,
        _ => UserRole.Custom
    };

    private static string MapAuthorityFromRole(UserRole role) => role switch
    {
        UserRole.Admin => "Admin",
        UserRole.Operator => "Operator",
        _ => "Viewer"
    };

    private static CoreLogLevel MapLevel(string level) => level.ToLowerInvariant() switch
    {
        "error" or "fatal" or "critical" => CoreLogLevel.Error,
        "warning" or "warn" => CoreLogLevel.Warning,
        "debug" => CoreLogLevel.Debug,
        _ => CoreLogLevel.Information
    };
}
