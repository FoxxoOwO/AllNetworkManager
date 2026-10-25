namespace AllNetworkManager.Core.Vendors;

using AllNetworkManager.Core.Devices;
using System.Collections.Generic;
using System.Threading;

public interface IVendorDeviceManager
{
    string VendorName { get; }
    string[] SupportedModels { get; }
    Task<bool> CanManageAsync(IDevice device);
    Task<DeviceInfo> GetDeviceInfoAsync(IDevice device, DeviceCredentials credentials, CancellationToken ct = default);
    Task<bool> SetNetworkConfigAsync(IDevice device, DeviceCredentials credentials, NetworkConfig config, CancellationToken ct = default);
    Task<bool> UpdateFirmwareAsync(IDevice device, DeviceCredentials credentials, FirmwareImage firmware, CancellationToken ct = default);
    Task<ConfigurationBackup> BackupConfigurationAsync(IDevice device, DeviceCredentials credentials, CancellationToken ct = default);
    Task<bool> RestoreConfigurationAsync(IDevice device, DeviceCredentials credentials, ConfigurationBackup backup, CancellationToken ct = default);
    Task<IEnumerable<UserAccount>> GetUsersAsync(IDevice device, DeviceCredentials credentials, CancellationToken ct = default);
    Task<bool> SetUserAsync(IDevice device, DeviceCredentials credentials, UserAccount user, CancellationToken ct = default);
    Task<bool> DeleteUserAsync(IDevice device, DeviceCredentials credentials, string username, CancellationToken ct = default);
    Task<DeviceLog[]> GetLogsAsync(IDevice device, DeviceCredentials credentials, LogFilter? filter = null, CancellationToken ct = default);
    Task RebootAsync(IDevice device, DeviceCredentials credentials, CancellationToken ct = default);
    Task FactoryResetAsync(IDevice device, DeviceCredentials credentials, CancellationToken ct = default);
}

public record DeviceInfo(
    string Manufacturer,
    string Model,
    string SerialNumber,
    string FirmwareVersion,
    string HardwareVersion,
    string MacAddress,
    DateTime? ManufactureDate,
    Dictionary<string, object> AdditionalInfo
);

public record NetworkConfig(
    string IpAddress,
    string SubnetMask,
    string Gateway,
    string? Dns1,
    string? Dns2,
    bool IsDhcp,
    int? VlanId,
    string? Hostname
);

public record FirmwareImage(
    string Version,
    string FileName,
    byte[] Data,
    string? ReleaseNotes,
    DateTime ReleaseDate,
    bool IsBeta,
    string[] CompatibleModels
);

public record ConfigurationBackup(
    string DeviceId,
    string DeviceModel,
    string FirmwareVersion,
    DateTime BackupDate,
    byte[] Data,
    string Format
);

public record UserAccount(
    string Username,
    string? Password,
    UserRole Role,
    bool IsEnabled,
    string? Email,
    DateTime? LastLogin,
    Dictionary<string, object> Permissions
);

public enum UserRole
{
    Admin,
    Operator,
    Viewer,
    Custom
}

public record DeviceLog(
    DateTime Timestamp,
    LogLevel Level,
    string Source,
    string Message,
    Dictionary<string, object>? Metadata
);

public enum LogLevel
{
    Debug,
    Information,
    Warning,
    Error,
    Critical
}

public record LogFilter(
    DateTime? From,
    DateTime? To,
    LogLevel? MinLevel,
    string? Source,
    string? SearchText,
    int MaxCount
);

public interface IVendorCapabilities
{
    bool SupportsNetworkConfig { get; }
    bool SupportsFirmwareUpdate { get; }
    bool SupportsConfigBackupRestore { get; }
    bool SupportsConfigRestore { get; }
    bool SupportsUserManagement { get; }
    bool SupportsLogs { get; }
    bool SupportsReboot { get; }
    bool SupportsFactoryReset { get; }
    bool SupportsPtz { get; }
    bool SupportsLiveView { get; }
    bool SupportsRecording { get; }
    bool SupportsAnalytics { get; }
}