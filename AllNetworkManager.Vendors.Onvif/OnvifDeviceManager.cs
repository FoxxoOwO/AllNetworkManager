namespace AllNetworkManager.Vendors.Onvif;

using AllNetworkManager.Core.Devices;
using AllNetworkManager.Core.Vendors;
using AllNetworkManager.Core.Onvif;
using AllNetworkManager.Core.Exceptions;
using Microsoft.Extensions.Logging;

/// <summary>
/// Vendor layer for any device that speaks standard ONVIF (Axis, Hanwha/Wisenet, Bosch, Dahua, Hikvision...).
/// Only standard ONVIF operations are implemented; vendor-specific extras throw UnsupportedOperation and the
/// capability flags keep the UI from offering them.
/// </summary>
public class OnvifDeviceManager : IVendorDeviceManager, IVendorCapabilities
{
    private readonly ILogger<OnvifDeviceManager> _logger;
    private readonly Func<string, DeviceCredentials?, IOnvifClient> _clientFactory;

    public string VendorName => "ONVIF (generic)";
    public string[] SupportedModels => Array.Empty<string>();

    public bool SupportsNetworkConfig => true;
    public bool SupportsFirmwareUpdate => false;
    public bool SupportsConfigBackupRestore => false;
    public bool SupportsConfigRestore => false;
    public bool SupportsUserManagement => true;
    public bool SupportsLogs => false;
    public bool SupportsReboot => true;
    public bool SupportsFactoryReset => true;
    public bool SupportsPtz => true;
    public bool SupportsLiveView => true;
    public bool SupportsRecording => false;
    public bool SupportsAnalytics => false;

    public OnvifDeviceManager(
        ILogger<OnvifDeviceManager> logger,
        Func<string, DeviceCredentials?, IOnvifClient> clientFactory)
    {
        _logger = logger;
        _clientFactory = clientFactory;
    }

    private IOnvifClient Client(IDevice device, DeviceCredentials credentials)
        => _clientFactory(EndpointFor(device), credentials);

    private static string EndpointFor(IDevice device)
    {
        if (device.ApiEndpoint != null && device.ApiEndpoint.Scheme.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return device.ApiEndpoint.ToString();

        return $"http://{device.IpAddress}:{device.Port ?? 80}/onvif/device_service";
    }

    public async Task<bool> CanManageAsync(IDevice device)
    {
        try
        {
            var info = await Client(device, new DeviceCredentials("", "")).GetDeviceInfoAsync();
            return !string.IsNullOrWhiteSpace(info.Manufacturer);
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
            var client = Client(device, credentials);
            var info = await client.GetDeviceInfoAsync(ct);
            var caps = await client.GetCapabilitiesAsync(ct);

            string mac = "";
            try
            {
                var ifaces = await client.GetNetworkInterfacesAsync(ct);
                mac = ifaces.Interfaces.FirstOrDefault()?.Name ?? "";
            }
            catch { }

            return new DeviceInfo(
                Manufacturer: info.Manufacturer,
                Model: info.Model,
                SerialNumber: info.SerialNumber,
                FirmwareVersion: info.FirmwareVersion,
                HardwareVersion: info.HardwareId,
                MacAddress: mac,
                ManufactureDate: info.ManufactureDate,
                AdditionalInfo: new Dictionary<string, object>
                {
                    ["capabilities"] = caps,
                    ["vendor"] = VendorName
                });
        }
        catch (Exception ex)
        {
            throw new DeviceManagerException($"ONVIF device info failed: {ex.Message}", device.Id, VendorName, ErrorCode.ProtocolError, ex);
        }
    }

    public async Task<bool> SetNetworkConfigAsync(IDevice device, DeviceCredentials credentials, NetworkConfig config, CancellationToken ct = default)
    {
        try
        {
            var client = Client(device, credentials);
            var current = await client.GetNetworkInterfacesAsync(ct);
            var target = current.Interfaces.FirstOrDefault();
            if (target?.Ipv4 == null)
                throw new ConfigurationException(device.Id, VendorName, "Device exposes no IPv4 interface");

            var updated = new OnvifNetworkInterfaces(current.Interfaces.Select(i =>
                i.Token != target.Token ? i : i with
                {
                    Ipv4 = i.Ipv4! with
                    {
                        Enabled = true,
                        Mode = config.IsDhcp ? OnvifIpConfigMode.DHCP : OnvifIpConfigMode.Static,
                        Address = config.IsDhcp ? i.Ipv4.Address : config.IpAddress,
                        PrefixLength = config.IsDhcp ? i.Ipv4.PrefixLength : PrefixLength(config.SubnetMask),
                        Gateway = config.IsDhcp ? i.Ipv4.Gateway : config.Gateway,
                        DnsServers = config.IsDhcp
                            ? i.Ipv4.DnsServers
                            : new[] { config.Dns1, config.Dns2 }
                                .Where(x => !string.IsNullOrWhiteSpace(x))
                                .Select(x => x!)
                                .ToArray()
                    }
                }));

            return await client.SetNetworkInterfacesAsync(updated, ct);
        }
        catch (Exception ex) when (ex is not ConfigurationException)
        {
            throw new ConfigurationException(device.Id, VendorName, $"Network configuration failed: {ex.Message}", ex);
        }
    }

    public Task<bool> UpdateFirmwareAsync(IDevice device, DeviceCredentials credentials, FirmwareImage firmware, CancellationToken ct = default)
        => throw new DeviceManagerException(
            "Firmware update is not standardized in ONVIF; use the vendor layer.",
            device.Id, VendorName, ErrorCode.UnsupportedOperation);

    public Task<ConfigurationBackup> BackupConfigurationAsync(IDevice device, DeviceCredentials credentials, CancellationToken ct = default)
        => throw new DeviceManagerException(
            "Configuration backup is not standardized in ONVIF; use the vendor layer.",
            device.Id, VendorName, ErrorCode.UnsupportedOperation);

    public Task<bool> RestoreConfigurationAsync(IDevice device, DeviceCredentials credentials, ConfigurationBackup backup, CancellationToken ct = default)
        => throw new DeviceManagerException(
            "Configuration restore is not standardized in ONVIF; use the vendor layer.",
            device.Id, VendorName, ErrorCode.UnsupportedOperation);

    public async Task<IEnumerable<UserAccount>> GetUsersAsync(IDevice device, DeviceCredentials credentials, CancellationToken ct = default)
    {
        try
        {
            var users = await Client(device, credentials).GetUsersAsync(ct);
            return users.Users.Select(u => new UserAccount(
                Username: u.Username,
                Password: null,
                Role: u.Level switch
                {
                    OnvifUserLevel.Administrator => UserRole.Admin,
                    OnvifUserLevel.Operator => UserRole.Operator,
                    _ => UserRole.Viewer
                },
                IsEnabled: true,
                Email: null,
                LastLogin: null,
                Permissions: new Dictionary<string, object> { ["onvifLevel"] = u.Level.ToString() }));
        }
        catch (Exception ex)
        {
            throw new DeviceManagerException($"Reading users failed: {ex.Message}", device.Id, VendorName, ErrorCode.ProtocolError, ex);
        }
    }

    public async Task<bool> SetUserAsync(IDevice device, DeviceCredentials credentials, UserAccount user, CancellationToken ct = default)
    {
        var level = user.Role switch
        {
            UserRole.Admin => OnvifUserLevel.Administrator,
            UserRole.Operator => OnvifUserLevel.Operator,
            _ => OnvifUserLevel.MediaUser
        };

        return await Client(device, credentials).CreateUserAsync(new OnvifUser(user.Username, level, user.Password), ct);
    }

    public async Task<bool> DeleteUserAsync(IDevice device, DeviceCredentials credentials, string username, CancellationToken ct = default)
        => await Client(device, credentials).DeleteUserAsync(username, ct);

    public Task<DeviceLog[]> GetLogsAsync(IDevice device, DeviceCredentials credentials, LogFilter? filter = null, CancellationToken ct = default)
    {
        _logger.LogDebug("ONVIF has no log retrieval; returning empty set for {Device}", device.Id);
        return Task.FromResult(Array.Empty<DeviceLog>());
    }

    public async Task RebootAsync(IDevice device, DeviceCredentials credentials, CancellationToken ct = default)
        => await Client(device, credentials).RebootAsync(ct);

    public async Task FactoryResetAsync(IDevice device, DeviceCredentials credentials, CancellationToken ct = default)
        => await Client(device, credentials).FactoryResetAsync(ct);

    private static string PrefixLength(string subnetMask)
    {
        if (!System.Net.IPAddress.TryParse(subnetMask, out var ip)) return "24";
        var bytes = ip.GetAddressBytes();
        int cidr = 0;
        foreach (var b in bytes)
        {
            for (int i = 7; i >= 0; i--)
            {
                if ((b & (1 << i)) != 0) cidr++;
                else return cidr.ToString();
            }
        }
        return cidr.ToString();
    }
}
