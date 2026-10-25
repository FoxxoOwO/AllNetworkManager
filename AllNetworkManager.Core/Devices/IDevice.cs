namespace AllNetworkManager.Core.Devices;

public interface IDevice
{
    string Id { get; }
    string Name { get; }
    string IpAddress { get; }
    int? Port { get; }
    string MacAddress { get; }
    string Manufacturer { get; }
    string Model { get; }
    string FirmwareVersion { get; }
    string HardwareVersion { get; }
    string SerialNumber { get; }
    DeviceType Type { get; }
    DeviceStatus Status { get; }
    DateTime DiscoveredAt { get; }
    DateTime LastSeen { get; }
    Dictionary<string, object> Properties { get; }
    IEnumerable<NetworkInterface> NetworkInterfaces { get; }
    Uri? WebInterfaceUrl { get; }
    Uri? ApiEndpoint { get; }
}

public enum DeviceType
{
    Unknown,
    Camera,
    Switch,
    Router,
    Recorder,
    Encoder,
    Decoder,
    AccessControl,
    Intercom,
    Sensor,
    Other
}

public enum DeviceStatus
{
    Unknown,
    Online,
    Offline,
    Unreachable,
    AuthRequired,
    Error,
    Updating
}

public record NetworkInterface(
    string Name,
    string IpAddress,
    string SubnetMask,
    string? Gateway,
    string? Dns1,
    string? Dns2,
    bool IsDhcp,
    string MacAddress,
    int? VlanId,
    int SpeedMbps,
    bool IsUp
);

public record DeviceCredentials(
    string Username,
    string Password,
    AuthenticationType AuthType = AuthenticationType.Basic
);

public enum AuthenticationType
{
    Basic,
    Digest,
    Bearer,
    ApiKey,
    Certificate,
    WindowsIntegrated
}