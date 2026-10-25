namespace AllNetworkManager.Core.Onvif;

using System.Collections.Generic;
using System.IO;
using System.Threading;

public interface IOnvifClient
{
    string DeviceAddress { get; }
    Task<OnvifDeviceInfo> GetDeviceInfoAsync(CancellationToken ct = default);
    Task<OnvifNetworkInterfaces> GetNetworkInterfacesAsync(CancellationToken ct = default);
    Task<bool> SetNetworkInterfacesAsync(OnvifNetworkInterfaces config, CancellationToken ct = default);
    Task<SystemDateTime> GetSystemDateTimeAsync(CancellationToken ct = default);
    Task<bool> SetSystemDateTimeAsync(SystemDateTime dateTime, CancellationToken ct = default);
    Task<OnvifUsers> GetUsersAsync(CancellationToken ct = default);
    Task<bool> CreateUserAsync(OnvifUser user, CancellationToken ct = default);
    Task<bool> DeleteUserAsync(string username, CancellationToken ct = default);
    Task<bool> SetUserPasswordAsync(string username, string password, CancellationToken ct = default);
    Task<OnvifCapabilities> GetCapabilitiesAsync(CancellationToken ct = default);
    Task RebootAsync(CancellationToken ct = default);
    Task FactoryResetAsync(CancellationToken ct = default);
    Task<Stream> GetSnapshotUriAsync(string profileToken, CancellationToken ct = default);
    Task<Uri> GetStreamUriAsync(string profileToken, StreamType streamType, CancellationToken ct = default);
    Task<PtzStatus> GetPtzStatusAsync(string profileToken, CancellationToken ct = default);
    Task<bool> PtzMoveAsync(string profileToken, PtzVector vector, CancellationToken ct = default);
    Task<bool> PtzStopAsync(string profileToken, CancellationToken ct = default);
    Task<OnvifRecordingConfig> GetRecordingConfigAsync(CancellationToken ct = default);
    Task<OnvifAnalyticsConfig> GetAnalyticsConfigAsync(CancellationToken ct = default);
}

public record OnvifDeviceInfo(
    string Manufacturer,
    string Model,
    string FirmwareVersion,
    string SerialNumber,
    string HardwareId,
    DateTime? ManufactureDate
);

public record OnvifNetworkInterfaces(
    IEnumerable<OnvifNetworkInterface> Interfaces
);

public record OnvifNetworkInterface(
    string Token,
    string Name,
    bool Enabled,
    OnvifIpv4Config? Ipv4,
    OnvifIpv6Config? Ipv6,
    OnvifLinkConfig? Link
);

public record OnvifIpv4Config(
    bool Enabled,
    OnvifIpConfigMode Mode,
    string? Address,
    string? PrefixLength,
    string? Gateway,
    IEnumerable<string>? DnsServers
);

public record OnvifIpv6Config(
    bool Enabled,
    OnvifIpConfigMode Mode,
    IEnumerable<OnvifIpv6Address> Addresses,
    string? Gateway,
    IEnumerable<string>? DnsServers
);

public record OnvifIpv6Address(
    string Address,
    int PrefixLength,
    OnvifAddressState State
);

public record OnvifLinkConfig(
    string Name,
    bool Enabled,
    OnvifLinkSpeed? Speed,
    bool AutoNegotiation
);

public record OnvifLinkSpeed(
    int SpeedMbps,
    bool Duplex
);

public record OnvifCapabilities(
    bool Device,
    bool Events,
    bool Imaging,
    bool Media,
    bool Ptz,
    bool Recording,
    bool Search,
    bool Replay,
    bool Analytics,
    bool DeviceIO
);

public record SystemDateTime(
    DateTime UtcTime,
    bool DaylightSavings,
    string TimeZone
);

public record OnvifUsers(
    IEnumerable<OnvifUser> Users
);

public record OnvifUser(
    string Username,
    OnvifUserLevel Level,
    string? Password
);

public enum OnvifUserLevel
{
    Administrator,
    Operator,
    MediaUser,
    Anonymous,
    Extended
}

public record OnvifRecordingConfig(
    bool Enabled,
    IEnumerable<OnvifRecordingJob> Jobs
);

public record OnvifRecordingJob(
    string Token,
    string Name,
    bool Enabled,
    string ProfileToken,
    OnvifRecordingSchedule Schedule
);

public record OnvifRecordingSchedule(
    IEnumerable<OnvifTimeRange> TimeRanges
);

public record OnvifTimeRange(
    TimeOnly Start,
    TimeOnly End,
    DayOfWeek[] Days
);

public record OnvifAnalyticsConfig(
    bool Enabled,
    IEnumerable<OnvifAnalyticsEngine> Engines
);

public record OnvifAnalyticsEngine(
    string Token,
    string Name,
    string Type,
    bool Enabled,
    IEnumerable<OnvifAnalyticsModule> Modules
);

public record OnvifAnalyticsModule(
    string Token,
    string Name,
    string Type,
    bool Enabled,
    Dictionary<string, object> Parameters
);

public enum OnvifIpConfigMode
{
    Static,
    DHCP,
    AutoIP,
    LinkLocal
}

public enum OnvifAddressState
{
    Preferred,
    Deprecated,
    Invalid,
    Inaccessible,
    Unknown
}

public enum StreamType
{
    RTP_Unicast,
    RTP_Multicast,
    RTP_RTSP_HTTP,
    RTP_RTSP_TCP
}

public record PtzVector(
    double? PanTiltX,
    double? PanTiltY,
    double? Zoom
);

public record PtzStatus(
    string ProfileToken,
    PtzPosition Position,
    PtzVector? MoveStatus,
    string? Error,
    DateTime UtcTime
);

public record PtzPosition(
    double Pan,
    double Tilt,
    double Zoom
);