namespace AllNetworkManager.Vendors.Microsens;

using System.Net;
using System.Net.Sockets;
using AllNetworkManager.Core.Devices;
using AllNetworkManager.Core.Vendors;
using AllNetworkManager.Core.Exceptions;
using Microsoft.Extensions.Logging;

/// <summary>
/// MICROSENS vendor layer. The vendor tool only does IP configuration over its proprietary UDP protocol, so
/// that is all this layer claims: no users, logs, firmware, backup or reboot (the switches themselves expose
/// those over HTTPS/SNMP, which this app does not implement yet).
/// </summary>
public class MicrosensDeviceManager : IVendorDeviceManager, IVendorCapabilities
{
    public const string FrameKey = "microsens.frame";

    private readonly ILogger<MicrosensDeviceManager> _logger;

    public string VendorName => "MICROSENS";
    public string[] SupportedModels => new[] { "MS", "XMS", "G6", "G7", "FTTO", "PLM", "PL+" };

    public bool SupportsNetworkConfig => true;
    public bool SupportsFirmwareUpdate => false;
    public bool SupportsConfigBackupRestore => false;
    public bool SupportsConfigRestore => false;
    public bool SupportsUserManagement => false;
    public bool SupportsLogs => false;
    public bool SupportsReboot => false;
    public bool SupportsFactoryReset => false;
    public bool SupportsPtz => false;
    public bool SupportsLiveView => false;
    public bool SupportsRecording => false;
    public bool SupportsAnalytics => false;

    public MicrosensDeviceManager(ILogger<MicrosensDeviceManager> logger) => _logger = logger;

    public Task<bool> CanManageAsync(IDevice device)
    {
        if (device.Manufacturer.Contains("microsens", StringComparison.OrdinalIgnoreCase)) return Task.FromResult(true);
        if (device.Properties.ContainsKey(FrameKey)) return Task.FromResult(true);
        return Task.FromResult(false);
    }

    public Task<DeviceInfo> GetDeviceInfoAsync(IDevice device, DeviceCredentials credentials, CancellationToken ct = default)
    {
        var announce = RequireAnnounce(device);

        var extra = new Dictionary<string, object>
        {
            ["microsensVersion"] = announce.FrameVersion,
            ["sectioned"] = announce.Sectioned,
            ["dhcpMode"] = announce.DhcpMode,
            ["hostname"] = announce.Hostname,
            ["location"] = announce.Location,
            ["group"] = announce.Group,
            ["articleNumber"] = announce.ArticleNumber,
            ["subnetMask"] = announce.SubnetMask,
            ["gateway"] = announce.Gateway
        };

        return Task.FromResult(new DeviceInfo(
            Manufacturer: "MICROSENS",
            Model: string.IsNullOrWhiteSpace(announce.ArticleNumber) ? device.Model : announce.ArticleNumber,
            SerialNumber: announce.SerialNumber,
            FirmwareVersion: announce.FirmwareVersion,
            HardwareVersion: announce.HardwareVersion,
            MacAddress: announce.MacAddress,
            ManufactureDate: null,
            AdditionalInfo: extra));
    }

    public async Task<bool> SetNetworkConfigAsync(IDevice device, DeviceCredentials credentials, NetworkConfig config, CancellationToken ct = default)
    {
        var announce = RequireAnnounce(device);

        var password = string.IsNullOrEmpty(credentials.Password) ? "administrator" : credentials.Password;
        var dhcpMode = config.IsDhcp ? 1 : 0;

        var outgoing = MicrosensFrame.BuildSetIp(
            announce,
            config.IpAddress,
            config.SubnetMask,
            config.Gateway,
            hostname: announce.Hostname,
            dhcpMode: dhcpMode,
            password: password);

        try
        {
            var transport = new MicrosensTransport(_logger);
            var unicast = outgoing.Broadcast ? null : IPAddress.TryParse(announce.IpAddress, out var target) ? target : null;
            await transport.SendAsync(outgoing, unicast, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not DeviceManagerException)
        {
            throw new ConfigurationException(device.Id, VendorName, $"Kanál pro MICROSENS rámec se nepodařilo odeslat: {ex.Message}", ex);
        }

        var confirmed = await WaitForConfirmationAsync(announce.MacAddress, config.IpAddress, TimeSpan.FromSeconds(6), ct)
            .ConfigureAwait(false);

        if (!confirmed)
        {
            throw new ConfigurationException(device.Id, VendorName,
                $"Rámec odeslán, ale zařízení {announce.MacAddress} do 6 s nepotvrdilo novou IP {config.IpAddress}. " +
                "Zkontroluj heslo (u G6/G7 je výchozí 'administrator') a zda zařízení není mimo broadcast doménu.");
        }

        return true;
    }

    /// <summary>Asks switches to release their address and announce again — used to restore reachability.</summary>
    public async Task RequestReleaseAsync(CancellationToken ct = default)
    {
        var transport = new MicrosensTransport(_logger);
        await transport.ReleaseAsync(ct).ConfigureAwait(false);
    }

    private async Task<bool> WaitForConfirmationAsync(string mac, string expectedIp, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            using var udp = new UdpClient();
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, MicrosensFrame.DiscoveryPort));

            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                if (udp.Available == 0)
                {
                    await Task.Delay(80, ct).ConfigureAwait(false);
                    continue;
                }

                var result = await udp.ReceiveAsync(ct).ConfigureAwait(false);
                if (!MicrosensFrame.TryParse(result.Buffer, out var announce) || announce == null) continue;
                if (!string.Equals(announce.MacAddress, mac, StringComparison.OrdinalIgnoreCase)) continue;

                if (announce.IpAddress == expectedIp) return true;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug("MICROSENS confirmation listener failed: {Message}", ex.Message);
        }

        return false;
    }

    private static MicrosensAnnounce RequireAnnounce(IDevice device)
    {
        if (!device.Properties.TryGetValue(FrameKey, out var value))
            throw new DeviceManagerException(
                "Chybí announce rámec zařízení — MICROSENS protokol potřebuje data z posledního scanu. Spusť scan znovu.",
                device.Id, "MICROSENS", ErrorCode.UnsupportedOperation);

        var frame = value switch
        {
            byte[] bytes => bytes,
            string base64 when base64.Length > 0 => SafeBase64(base64),
            _ => null
        };

        if (frame == null || !MicrosensFrame.TryParse(frame, out var announce) || announce == null)
            throw new DeviceManagerException(
                "Uložený MICROSENS rámec je nečitelný nebo poškozený. Spusť scan znovu.",
                device.Id, "MICROSENS", ErrorCode.ProtocolError);

        return announce;
    }

    private static byte[]? SafeBase64(string text)
    {
        try { return Convert.FromBase64String(text); }
        catch { return null; }
    }

    public Task<bool> UpdateFirmwareAsync(IDevice device, DeviceCredentials credentials, FirmwareImage firmware, CancellationToken ct = default)
        => throw new DeviceManagerException("MICROSENS firmware se nahrává přes HTTPS/Web UI, ne tímto protokolem.",
            device.Id, VendorName, ErrorCode.UnsupportedOperation);

    public Task<ConfigurationBackup> BackupConfigurationAsync(IDevice device, DeviceCredentials credentials, CancellationToken ct = default)
        => throw new DeviceManagerException("MICROSENS záloha konfigurace není v tomto protokolu dostupná.",
            device.Id, VendorName, ErrorCode.UnsupportedOperation);

    public Task<bool> RestoreConfigurationAsync(IDevice device, DeviceCredentials credentials, ConfigurationBackup backup, CancellationToken ct = default)
        => throw new DeviceManagerException("MICROSENS obnova konfigurace není v tomto protokolu dostupná.",
            device.Id, VendorName, ErrorCode.UnsupportedOperation);

    public Task<IEnumerable<UserAccount>> GetUsersAsync(IDevice device, DeviceCredentials credentials, CancellationToken ct = default)
        => throw new DeviceManagerException("MICROSENS správa uživatelů není v tomto protokolu dostupná.",
            device.Id, VendorName, ErrorCode.UnsupportedOperation);

    public Task<bool> SetUserAsync(IDevice device, DeviceCredentials credentials, UserAccount user, CancellationToken ct = default)
        => throw new DeviceManagerException("MICROSENS správa uživatelů není v tomto protokolu dostupná.",
            device.Id, VendorName, ErrorCode.UnsupportedOperation);

    public Task<bool> DeleteUserAsync(IDevice device, DeviceCredentials credentials, string username, CancellationToken ct = default)
        => throw new DeviceManagerException("MICROSENS správa uživatelů není v tomto protokolu dostupná.",
            device.Id, VendorName, ErrorCode.UnsupportedOperation);

    public Task<DeviceLog[]> GetLogsAsync(IDevice device, DeviceCredentials credentials, LogFilter? filter = null, CancellationToken ct = default)
        => throw new DeviceManagerException("MICROSENS logy nejsou v tomto protokolu dostupné.",
            device.Id, VendorName, ErrorCode.UnsupportedOperation);

    public Task RebootAsync(IDevice device, DeviceCredentials credentials, CancellationToken ct = default)
        => throw new DeviceManagerException("MICROSENS reboot není v tomto protokolu dostupný.",
            device.Id, VendorName, ErrorCode.UnsupportedOperation);

    public Task FactoryResetAsync(IDevice device, DeviceCredentials credentials, CancellationToken ct = default)
        => throw new DeviceManagerException("MICROSENS factory reset není v tomto protokolu dostupný.",
            device.Id, VendorName, ErrorCode.UnsupportedOperation);
}
