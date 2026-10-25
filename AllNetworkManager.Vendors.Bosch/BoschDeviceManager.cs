namespace AllNetworkManager.Vendors.Bosch;

using AllNetworkManager.Core.Devices;
using AllNetworkManager.Core.Vendors;
using AllNetworkManager.Core.Onvif;
using AllNetworkManager.Core.Exceptions;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Net;

public class BoschDeviceManager : IVendorDeviceManager, IVendorCapabilities
{
    private readonly ILogger<BoschDeviceManager> _logger;
    private readonly Func<string, DeviceCredentials?, IOnvifClient> _clientFactory;
    private readonly HttpClient _httpClient;
    private readonly BoschVideoSdkWrapper? _videoSdk;

    public string VendorName => "Bosch";
    public string[] SupportedModels => new[]
    {
        "MIC ULTRA II", "Kitkat3 II", "Larnaca", "PIKE", "Hibiscus",
        "Polaris 5000", "Polaris 7000", "Sirius 5000", "GEN6 AUTODOME",
        "Deneb", "Halo", "Halo micro", "Sentinel", "AVIOTEC",
        "Halo Bullet", "Kamino", "Cyclops", "GEN6 CPP16 Gen2",
        "HOME_Gen2Indoor", "HOME_Gen2Outdoor", "Webb", "Nicosia",
        "Lefkosia", "Polaris 2.0", "Kamino 2.0", "Gen6 Phase 3",
        "Larnaca II", "Merlin 3", "MICU3", "Atlas-Nexus"
    };

    public bool SupportsNetworkConfig => true;
    public bool SupportsFirmwareUpdate => true;
    public bool SupportsConfigBackupRestore => true;
    public bool SupportsConfigRestore => true;
    public bool SupportsUserManagement => true;
    public bool SupportsLogs => true;
    public bool SupportsReboot => true;
    public bool SupportsFactoryReset => true;
    public bool SupportsPtz => true;
    public bool SupportsLiveView => true;
    public bool SupportsRecording => true;
    public bool SupportsAnalytics => true;

    public BoschDeviceManager(
        ILogger<BoschDeviceManager> logger,
        Func<string, DeviceCredentials?, IOnvifClient> clientFactory,
        HttpClient httpClient,
        BoschVideoSdkWrapper? videoSdk = null)
    {
        _logger = logger;
        _clientFactory = clientFactory;
        _httpClient = httpClient;
        _videoSdk = videoSdk;
    }


    private IOnvifClient Onvif(IDevice device, DeviceCredentials? credentials = null)
    {
        var endpoint = device.ApiEndpoint?.Scheme.StartsWith("http", StringComparison.OrdinalIgnoreCase) == true
            ? device.ApiEndpoint.ToString()
            : $"http://{device.IpAddress}:{device.Port ?? 80}/onvif/device_service";
        return _clientFactory(endpoint, credentials);
    }

    public async Task<bool> CanManageAsync(IDevice device)
    {
        if (string.Equals(device.Manufacturer, "Bosch", StringComparison.OrdinalIgnoreCase))
            return true;

        if (SupportedModels.Any(m => device.Model.Contains(m, StringComparison.OrdinalIgnoreCase)))
            return true;

        try
        {
            var info = await Onvif(device).GetDeviceInfoAsync();
            return info.Manufacturer.Contains("Bosch", StringComparison.OrdinalIgnoreCase);
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
            var onvif = Onvif(device, credentials);
            var onvifInfo = await onvif.GetDeviceInfoAsync(ct);
            var caps = await onvif.GetCapabilitiesAsync(ct);
            var netIfaces = await onvif.GetNetworkInterfacesAsync(ct);

            var additionalInfo = new Dictionary<string, object>
            {
                ["capabilities"] = caps,
                ["networkInterfaces"] = netIfaces.Interfaces
            };

            if (_videoSdk != null)
            {
                var sdkInfo = await _videoSdk.GetDeviceInfoAsync(device.IpAddress, credentials);
                additionalInfo["videoSdk"] = sdkInfo;
            }

            return new DeviceInfo(
                Manufacturer: onvifInfo.Manufacturer,
                Model: onvifInfo.Model,
                SerialNumber: onvifInfo.SerialNumber,
                FirmwareVersion: onvifInfo.FirmwareVersion,
                HardwareVersion: onvifInfo.HardwareId,
                MacAddress: netIfaces.Interfaces.FirstOrDefault()?.Link?.Name ?? "",
                ManufactureDate: onvifInfo.ManufactureDate,
                AdditionalInfo: additionalInfo
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get device info for {DeviceId}", device.Id);
            throw new DeviceManagerException("Failed to get device info", device.Id, VendorName, ErrorCode.ProtocolError, ex);
        }
    }

    public async Task<bool> SetNetworkConfigAsync(IDevice device, DeviceCredentials credentials, NetworkConfig config, CancellationToken ct = default)
    {
        try
        {
            var interfaces = await Onvif(device, credentials).GetNetworkInterfacesAsync(ct);
            var iface = interfaces.Interfaces.FirstOrDefault();
            if (iface == null) return false;

            var newConfig = new OnvifNetworkInterfaces(
                interfaces.Interfaces.Select(i => i.Token == iface.Token ? i with
                {
                    Ipv4 = i.Ipv4! with
                    {
                        Enabled = true,
                        Mode = config.IsDhcp ? OnvifIpConfigMode.DHCP : OnvifIpConfigMode.Static,
                        Address = config.IpAddress,
                        PrefixLength = GetPrefixLength(config.SubnetMask).ToString(),
                        Gateway = config.Gateway,
                        DnsServers = new[] { config.Dns1, config.Dns2 }.Where(x => !string.IsNullOrEmpty(x)).Select(x => x!)
                    }
                } : i)
            );

            var result = await Onvif(device, credentials).SetNetworkInterfacesAsync(newConfig, ct);
            
            if (result && !string.IsNullOrEmpty(config.Hostname))
            {
                await SetHostnameAsync(device, credentials, config.Hostname, ct);
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set network config for {DeviceId}", device.Id);
            throw new ConfigurationException(device.Id, VendorName, "Network configuration failed", ex);
        }
    }

    private async Task SetHostnameAsync(IDevice device, DeviceCredentials credentials, string hostname, CancellationToken ct)
    {
        try
        {
            var url = $"http://{device.IpAddress}/cgi-bin/configManager.cgi?action=setConfig&General.Hostname={Uri.EscapeDataString(hostname)}";
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            SetAuth(request, credentials);
            await _httpClient.SendAsync(request, ct);
        }
        catch { }
    }

    public async Task<bool> UpdateFirmwareAsync(IDevice device, DeviceCredentials credentials, FirmwareImage firmware, CancellationToken ct = default)
    {
        try
        {
            _logger.LogInformation("Starting firmware update for {DeviceId} to version {Version}", device.Id, firmware.Version);

            var url = $"http://{device.IpAddress}/cgi-bin/firmware.cgi";
            var content = new MultipartFormDataContent();
            content.Add(new ByteArrayContent(firmware.Data), "firmware", firmware.FileName);
            
            var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            SetAuth(request, credentials);

            var response = await _httpClient.SendAsync(request, ct);
            
            if (!response.IsSuccessStatusCode)
            {
                throw new FirmwareUpdateException(device.Id, VendorName, $"Firmware upload failed: {response.StatusCode}");
            }

            await WaitForRebootAsync(device, ct);
            return true;
        }
        catch (Exception ex) when (ex is not FirmwareUpdateException)
        {
            _logger.LogError(ex, "Firmware update failed for {DeviceId}", device.Id);
            throw new FirmwareUpdateException(device.Id, VendorName, "Firmware update failed", ex);
        }
    }

    public async Task<ConfigurationBackup> BackupConfigurationAsync(IDevice device, DeviceCredentials credentials, CancellationToken ct = default)
    {
        try
        {
            var url = $"http://{device.IpAddress}/cgi-bin/configManager.cgi?action=export";
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            SetAuth(request, credentials);

            var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var data = await response.Content.ReadAsByteArrayAsync(ct);
            
            return new ConfigurationBackup(
                DeviceId: device.Id,
                DeviceModel: device.Model,
                FirmwareVersion: device.FirmwareVersion,
                BackupDate: DateTime.UtcNow,
                Data: data,
                Format: "bosch-config"
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Configuration backup failed for {DeviceId}", device.Id);
            throw new ConfigurationException(device.Id, VendorName, "Backup failed", ex);
        }
    }

    public async Task<bool> RestoreConfigurationAsync(IDevice device, DeviceCredentials credentials, ConfigurationBackup backup, CancellationToken ct = default)
    {
        try
        {
            var url = $"http://{device.IpAddress}/cgi-bin/configManager.cgi?action=import";
            var content = new MultipartFormDataContent();
            content.Add(new ByteArrayContent(backup.Data), "config", "config.bin");
            
            var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            SetAuth(request, credentials);

            var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            await WaitForRebootAsync(device, ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Configuration restore failed for {DeviceId}", device.Id);
            throw new ConfigurationException(device.Id, VendorName, "Restore failed", ex);
        }
    }

    public async Task<IEnumerable<UserAccount>> GetUsersAsync(IDevice device, DeviceCredentials credentials, CancellationToken ct = default)
    {
        try
        {
            var users = await Onvif(device, credentials).GetUsersAsync(ct);
            return users.Users.Select(u => new UserAccount(
                Username: u.Username,
                Password: null,
                Role: MapRole(u.Level),
                IsEnabled: true,
                Email: null,
                LastLogin: null,
                Permissions: new Dictionary<string, object> { ["onvifLevel"] = u.Level.ToString() }
            ));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get users for {DeviceId}", device.Id);
            return Array.Empty<UserAccount>();
        }
    }

    public async Task<bool> SetUserAsync(IDevice device, DeviceCredentials credentials, UserAccount user, CancellationToken ct = default)
    {
        try
        {
            var onvifUser = new OnvifUser(
                Username: user.Username,
                Level: MapRole(user.Role),
                Password: user.Password
            );
            return await Onvif(device, credentials).CreateUserAsync(onvifUser, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set user for {DeviceId}", device.Id);
            return false;
        }
    }

    public async Task<bool> DeleteUserAsync(IDevice device, DeviceCredentials credentials, string username, CancellationToken ct = default)
    {
        try
        {
            return await Onvif(device, credentials).DeleteUserAsync(username, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete user for {DeviceId}", device.Id);
            return false;
        }
    }

    public async Task<DeviceLog[]> GetLogsAsync(IDevice device, DeviceCredentials credentials, LogFilter? filter = null, CancellationToken ct = default)
    {
        try
        {
            var url = $"http://{device.IpAddress}/cgi-bin/log.cgi?action=get";
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            SetAuth(request, credentials);

            var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync(ct);
            return ParseLogs(content, filter);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get logs for {DeviceId}", device.Id);
            return Array.Empty<DeviceLog>();
        }
    }

    public async Task RebootAsync(IDevice device, DeviceCredentials credentials, CancellationToken ct = default)
    {
        await Onvif(device, credentials).RebootAsync(ct);
    }

    public async Task FactoryResetAsync(IDevice device, DeviceCredentials credentials, CancellationToken ct = default)
    {
        await Onvif(device, credentials).FactoryResetAsync(ct);
    }

    private void SetAuth(HttpRequestMessage request, DeviceCredentials credentials)
    {
        switch (credentials.AuthType)
        {
            case AuthenticationType.Basic:
                var token = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($"{credentials.Username}:{credentials.Password}"));
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", token);
                break;
            case AuthenticationType.Digest:
                // Would need digest auth implementation
                break;
        }
    }

    private int GetPrefixLength(string subnetMask)
    {
        var bytes = IPAddress.Parse(subnetMask).GetAddressBytes();
        int cidr = 0;
        foreach (var b in bytes)
            for (int i = 7; i >= 0; i--)
                if ((b & (1 << i)) != 0) cidr++; else return cidr;
        return cidr;
    }

    private UserRole MapRole(OnvifUserLevel level) => level switch
    {
        OnvifUserLevel.Administrator => UserRole.Admin,
        OnvifUserLevel.Operator => UserRole.Operator,
        OnvifUserLevel.MediaUser => UserRole.Viewer,
        _ => UserRole.Viewer
    };

    private OnvifUserLevel MapRole(UserRole role) => role switch
    {
        UserRole.Admin => OnvifUserLevel.Administrator,
        UserRole.Operator => OnvifUserLevel.Operator,
        _ => OnvifUserLevel.MediaUser
    };

    private async Task WaitForRebootAsync(IDevice device, CancellationToken ct)
    {
        await Task.Delay(5000, ct);
        var endTime = DateTime.UtcNow.AddMinutes(2);
        while (DateTime.UtcNow < endTime)
        {
            try
            {
                await Onvif(device).GetDeviceInfoAsync(ct);
                return;
            }
            catch { await Task.Delay(2000, ct); }
        }
    }

    private DeviceLog[] ParseLogs(string content, LogFilter? filter)
    {
        // Parse Bosch log format
        var logs = new List<DeviceLog>();
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        
        foreach (var line in lines)
        {
            // Parse typical Bosch log format: [timestamp] [level] [source] message
            var match = System.Text.RegularExpressions.Regex.Match(line, @"\[(.*?)\]\s+\[(.*?)\]\s+\[(.*?)\]\s+(.*)");
            if (match.Success)
            {
                if (DateTime.TryParse(match.Groups[1].Value, out var timestamp))
                {
                    var level = ParseLogLevel(match.Groups[2].Value);
                    var source = match.Groups[3].Value;
                    var message = match.Groups[4].Value;
                    
                    if (filter?.MinLevel != null && level < filter.MinLevel) continue;
                    if (filter?.From != null && timestamp < filter.From) continue;
                    if (filter?.To != null && timestamp > filter.To) continue;
                    if (!string.IsNullOrEmpty(filter?.Source) && !source.Contains(filter.Source, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!string.IsNullOrEmpty(filter?.SearchText) && !message.Contains(filter.SearchText, StringComparison.OrdinalIgnoreCase)) continue;

                    logs.Add(new DeviceLog(timestamp, level, source, message, null));
                }
            }
        }

        return logs.OrderByDescending(l => l.Timestamp).Take(filter?.MaxCount ?? 1000).ToArray();
    }

    private AllNetworkManager.Core.Vendors.LogLevel ParseLogLevel(string level) => level.ToLowerInvariant() switch
    {
        "debug" => AllNetworkManager.Core.Vendors.LogLevel.Debug,
        "info" or "information" => AllNetworkManager.Core.Vendors.LogLevel.Information,
        "warn" or "warning" => AllNetworkManager.Core.Vendors.LogLevel.Warning,
        "error" => AllNetworkManager.Core.Vendors.LogLevel.Error,
        "critical" or "fatal" => AllNetworkManager.Core.Vendors.LogLevel.Critical,
        _ => AllNetworkManager.Core.Vendors.LogLevel.Information
    };
}