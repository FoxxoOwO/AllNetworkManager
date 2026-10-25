namespace AllNetworkManager.Core.Exceptions;

public class DeviceManagerException : Exception
{
    public string DeviceId { get; }
    public string Vendor { get; }
    public ErrorCode Code { get; }

    public DeviceManagerException(string message, string deviceId, string vendor, ErrorCode code = ErrorCode.Unknown, Exception? inner = null)
        : base(message, inner)
    {
        DeviceId = deviceId;
        Vendor = vendor;
        Code = code;
    }
}

public class DiscoveryException : Exception
{
    public string Subnet { get; }
    public string Protocol { get; }

    public DiscoveryException(string message, string subnet, string protocol, Exception? inner = null)
        : base(message, inner)
    {
        Subnet = subnet;
        Protocol = protocol;
    }
}

public class AuthenticationException : DeviceManagerException
{
    public AuthenticationException(string deviceId, string vendor, string message = "Authentication failed")
        : base(message, deviceId, vendor, ErrorCode.AuthenticationFailed) { }
}

public class DeviceUnreachableException : DeviceManagerException
{
    public DeviceUnreachableException(string deviceId, string vendor, string message = "Device unreachable")
        : base(message, deviceId, vendor, ErrorCode.DeviceUnreachable) { }
}

public class FirmwareUpdateException : DeviceManagerException
{
    public FirmwareUpdateException(string deviceId, string vendor, string message, Exception? inner = null)
        : base(message, deviceId, vendor, ErrorCode.FirmwareUpdateFailed, inner) { }
}

public class ConfigurationException : DeviceManagerException
{
    public ConfigurationException(string deviceId, string vendor, string message, Exception? inner = null)
        : base(message, deviceId, vendor, ErrorCode.ConfigurationFailed, inner) { }
}

public enum ErrorCode
{
    Unknown,
    AuthenticationFailed,
    DeviceUnreachable,
    FirmwareUpdateFailed,
    ConfigurationFailed,
    BackupFailed,
    RestoreFailed,
    UserManagementFailed,
    UnsupportedOperation,
    InvalidCredentials,
    Timeout,
    NetworkError,
    ProtocolError,
    DeviceBusy,
    InsufficientPermissions,
    InvalidFirmware,
    RollbackFailed
}