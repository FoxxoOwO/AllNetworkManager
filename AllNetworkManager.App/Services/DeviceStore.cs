namespace AllNetworkManager.App.Services;

using System.Globalization;
using System.IO;
using System.Text.Json;
using AllNetworkManager.Core.Devices;
using AllNetworkManager.Core.Discovery;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

/// <summary>One device as stored in the inventory (DTO used by the view models).</summary>
public class StoredDevice
{
    public string IpAddress { get; set; } = "";
    public int? Port { get; set; }
    public string Manufacturer { get; set; } = "";
    public string Model { get; set; } = "";
    public string? MacAddress { get; set; }
    public string? FirmwareVersion { get; set; }
    public string? HardwareVersion { get; set; }
    public string? SerialNumber { get; set; }

    /// <summary>Hostname as reported by the device (identity/param/property) or resolved by reverse DNS.</summary>
    public string? Hostname { get; set; }
    public string DiscoveryProtocol { get; set; } = "";
    public string? WebInterfaceUrl { get; set; }
    public string? ApiEndpoint { get; set; }
    public string? Username { get; set; }

    /// <summary>DPAPI-protected password, base64. Never plaintext.</summary>
    public string? ProtectedPassword { get; set; }

    public string? Notes { get; set; }
    public List<string> Tags { get; set; } = new();
    public DateTime FirstSeen { get; set; } = DateTime.UtcNow;
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;

    public DeviceCredentials Credentials => new(Username ?? "admin", CredentialProtector.Unprotect(ProtectedPassword));

    public DiscoveredDevice ToDiscovered() => new(
        IpAddress: IpAddress,
        Port: Port,
        Manufacturer: Manufacturer,
        Model: Model,
        MacAddress: MacAddress,
        FirmwareVersion: FirmwareVersion,
        HardwareVersion: HardwareVersion,
        SerialNumber: SerialNumber,
        Type: InferType(Manufacturer, Model),
        Properties: null,
        WebInterfaceUrl: string.IsNullOrWhiteSpace(WebInterfaceUrl) ? null : new Uri(WebInterfaceUrl),
        ApiEndpoint: string.IsNullOrWhiteSpace(ApiEndpoint) ? null : new Uri(ApiEndpoint),
        DiscoveryProtocol: DiscoveryProtocol);

    private static DeviceType InferType(string manufacturer, string model)
    {
        var text = $"{manufacturer} {model}";
        if (text.Contains("AXIS", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Bosch", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Wisenet", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Hanwha", StringComparison.OrdinalIgnoreCase))
            return DeviceType.Camera;

        return DeviceType.Unknown;
    }
}

/// <summary>
/// SQLite-backed device inventory: credentials, notes, tags, hostnames and discovery data survive
/// restarts, while rescans only refresh what the device reports. A single connection is kept open and
/// every operation is serialized, so background rescans and UI edits cannot corrupt each other.
/// </summary>
public class DeviceStore : IDisposable
{
    public const int MaxTagLength = 32;
    public const int MaxTagsPerDevice = 20;

    private const int SchemaVersion = 1;

    private readonly ILogger<DeviceStore> _logger;
    private readonly object _gate = new();
    private SqliteConnection? _connection;

    public DeviceStore(ILogger<DeviceStore> logger)
        : this(logger, DefaultDatabasePath())
    {
    }

    public DeviceStore(ILogger<DeviceStore> logger, string path)
    {
        _logger = logger;
        FilePath = path;
    }

    /// <summary>Path of the SQLite database file.</summary>
    public string FilePath { get; }

    /// <summary>Legacy JSON inventory from before SQLite, imported once and then kept next to the database.</summary>
    public string LegacyJsonPath =>
        Path.Combine(Path.GetDirectoryName(FilePath) ?? ".", "devices.json");

    public static string DefaultDatabasePath()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(root, "AllNetworkManager", "inventory.db");
    }

    // ---------------------------------------------------------------- lifecycle

    /// <summary>Opens the database, creates the schema and imports a legacy JSON inventory if present.</summary>
    public void Load()
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

                var connectionString = new SqliteConnectionStringBuilder
                {
                    DataSource = FilePath,
                    Mode = SqliteOpenMode.ReadWriteCreate,
                    Cache = SqliteCacheMode.Shared
                }.ToString();

                _connection = new SqliteConnection(connectionString);
                _connection.Open();

                Execute("PRAGMA journal_mode=WAL;");
                Execute("PRAGMA synchronous=NORMAL;");
                Execute("PRAGMA busy_timeout=5000;");
                Execute("PRAGMA foreign_keys=ON;");

                CreateSchema();
                ImportLegacyJson();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not open the inventory database at {Path}", FilePath);
                _connection?.Dispose();
                _connection = null;
            }
        }
    }

    /// <summary>Kept for compatibility: writes are applied immediately, nothing is buffered.</summary>
    public void Save()
    {
        lock (_gate)
        {
            try
            {
                Execute("PRAGMA wal_checkpoint(PASSIVE);");
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "WAL checkpoint failed");
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _connection?.Dispose();
            _connection = null;
        }
    }

    // ---------------------------------------------------------------- reads

    /// <summary>Copy of the inventory, safe to enumerate while a scan writes.</summary>
    public IReadOnlyList<StoredDevice> Snapshot()
    {
        lock (_gate)
        {
            try
            {
                using var command = CreateCommand(
                    "SELECT * FROM Devices ORDER BY IpAddress COLLATE NOCASE;");
                using var reader = command.ExecuteReader();

                var result = new List<StoredDevice>();
                while (reader.Read()) result.Add(Map(reader));
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read the inventory");
                return Array.Empty<StoredDevice>();
            }
        }
    }

    public StoredDevice? Find(string ipAddress)
    {
        lock (_gate)
        {
            try
            {
                using var command = CreateCommand("SELECT * FROM Devices WHERE IpAddress = $ip;");
                command.Parameters.AddWithValue("$ip", ipAddress);
                using var reader = command.ExecuteReader();
                return reader.Read() ? Map(reader) : null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read device {Ip}", ipAddress);
                return null;
            }
        }
    }

    // ---------------------------------------------------------------- writes

    /// <summary>Merges discovered devices into the store, preserving credentials/notes/tags.</summary>
    public void UpsertRange(IEnumerable<DiscoveredDevice> discovered)
    {
        lock (_gate)
        {
            if (_connection == null) return;

            try
            {
                using var transaction = _connection.BeginTransaction();

                foreach (var device in discovered)
                {
                    var hostname = HostnameResolver.FromProperties(device.Properties);
                    var existing = FindUnlocked(device.IpAddress, transaction);

                    using var command = CreateCommand(
                        """
                        INSERT INTO Devices
                            (IpAddress, Port, Manufacturer, Model, MacAddress, FirmwareVersion, HardwareVersion,
                             SerialNumber, Hostname, DiscoveryProtocol, WebInterfaceUrl, ApiEndpoint, Username,
                             ProtectedPassword, Notes, TagsCsv, PropertiesJson, FirstSeen, LastSeen)
                        VALUES
                            ($ip, $port, $manufacturer, $model, $mac, $firmware, $hardware,
                             $serial, $hostname, $protocol, $web, $api, $username,
                             $password, $notes, $tags, $properties, $firstSeen, $lastSeen)
                        ON CONFLICT(IpAddress) DO UPDATE SET
                            Port = COALESCE(excluded.Port, Port),
                            Manufacturer = CASE WHEN excluded.Manufacturer = '' THEN Manufacturer ELSE excluded.Manufacturer END,
                            Model = CASE WHEN excluded.Model = '' THEN Model ELSE excluded.Model END,
                            MacAddress = COALESCE(excluded.MacAddress, MacAddress),
                            FirmwareVersion = COALESCE(excluded.FirmwareVersion, FirmwareVersion),
                            HardwareVersion = COALESCE(excluded.HardwareVersion, HardwareVersion),
                            SerialNumber = COALESCE(excluded.SerialNumber, SerialNumber),
                            Hostname = COALESCE(excluded.Hostname, Hostname),
                            DiscoveryProtocol = CASE WHEN excluded.DiscoveryProtocol = '' THEN DiscoveryProtocol ELSE excluded.DiscoveryProtocol END,
                            WebInterfaceUrl = COALESCE(excluded.WebInterfaceUrl, WebInterfaceUrl),
                            ApiEndpoint = COALESCE(excluded.ApiEndpoint, ApiEndpoint),
                            PropertiesJson = CASE WHEN excluded.PropertiesJson = '{}' THEN PropertiesJson ELSE excluded.PropertiesJson END,
                            LastSeen = excluded.LastSeen;
                        """,
                        transaction);

                    command.Parameters.AddWithValue("$ip", device.IpAddress);
                    command.Parameters.AddWithValue("$port", (object?)device.Port ?? DBNull.Value);
                    command.Parameters.AddWithValue("$manufacturer", device.Manufacturer ?? "");
                    command.Parameters.AddWithValue("$model", device.Model ?? "");
                    command.Parameters.AddWithValue("$mac", (object?)device.MacAddress ?? DBNull.Value);
                    command.Parameters.AddWithValue("$firmware", (object?)device.FirmwareVersion ?? DBNull.Value);
                    command.Parameters.AddWithValue("$hardware", (object?)device.HardwareVersion ?? DBNull.Value);
                    command.Parameters.AddWithValue("$serial", (object?)device.SerialNumber ?? DBNull.Value);
                    command.Parameters.AddWithValue("$hostname", (object?)hostname ?? DBNull.Value);
                    command.Parameters.AddWithValue("$protocol", device.DiscoveryProtocol ?? "");
                    command.Parameters.AddWithValue("$web", (object?)device.WebInterfaceUrl?.ToString() ?? DBNull.Value);
                    command.Parameters.AddWithValue("$api", (object?)device.ApiEndpoint?.ToString() ?? DBNull.Value);
                    command.Parameters.AddWithValue("$username", (object?)existing?.Username ?? DBNull.Value);
                    command.Parameters.AddWithValue("$password", (object?)existing?.ProtectedPassword ?? DBNull.Value);
                    command.Parameters.AddWithValue("$notes", (object?)existing?.Notes ?? DBNull.Value);
                    command.Parameters.AddWithValue("$tags", JoinTags(existing?.Tags));
                    command.Parameters.AddWithValue("$properties", SerializeProperties(device.Properties));
                    command.Parameters.AddWithValue("$firstSeen", existing?.FirstSeen ?? DateTime.UtcNow);
                    command.Parameters.AddWithValue("$lastSeen", DateTime.UtcNow);

                    command.ExecuteNonQuery();
                }

                transaction.Commit();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not merge {Count} discovered devices", discovered.Count());
            }
        }
    }

    /// <summary>Stores the free-text note for a device. A blank value clears it.</summary>
    public void SetNotes(string ipAddress, string? notes)
    {
        ExecuteUpdate("UPDATE Devices SET Notes = $value WHERE IpAddress = $ip;", ipAddress,
            string.IsNullOrWhiteSpace(notes) ? DBNull.Value : notes.Trim());
    }

    /// <summary>Stores the normalized tag list for a device.</summary>
    public void SetTags(string ipAddress, IEnumerable<string> tags)
    {
        ExecuteUpdate("UPDATE Devices SET TagsCsv = $value WHERE IpAddress = $ip;", ipAddress, JoinTags(tags));
    }

    /// <summary>Stores a hostname the device reported or reverse DNS resolved.</summary>
    public void SetHostname(string ipAddress, string? hostname)
    {
        if (string.IsNullOrWhiteSpace(hostname)) return;
        ExecuteUpdate("UPDATE Devices SET Hostname = $value WHERE IpAddress = $ip;", ipAddress, hostname.Trim());
    }

    /// <summary>Stores credentials; the password is encrypted with DPAPI before it reaches the database.</summary>
    public void SetCredentials(string ipAddress, string username, string password)
    {
        lock (_gate)
        {
            try
            {
                using var command = CreateCommand(
                    "UPDATE Devices SET Username = $user, ProtectedPassword = $password WHERE IpAddress = $ip;");
                command.Parameters.AddWithValue("$user", username ?? "");
                command.Parameters.AddWithValue("$password", (object?)CredentialProtector.Protect(password) ?? DBNull.Value);
                command.Parameters.AddWithValue("$ip", ipAddress);
                command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not store credentials for {Ip}", ipAddress);
            }
        }
    }

    /// <summary>Hostname reported inside discovery properties (each protocol names it differently).</summary>
    public static string? HostnameFromProperties(IDictionary<string, object>? properties)
        => HostnameResolver.FromProperties(properties);

    /// <summary>Trims, drops blanks, removes case-insensitive duplicates and enforces the limits.</summary>
    public static List<string> NormalizeTags(IEnumerable<string>? tags)
    {
        var result = new List<string>();
        if (tags == null) return result;

        foreach (var tag in tags)
        {
            var trimmed = tag?.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            if (trimmed.Length > MaxTagLength) trimmed = trimmed[..MaxTagLength];
            if (result.Any(t => t.Equals(trimmed, StringComparison.OrdinalIgnoreCase))) continue;

            result.Add(trimmed);
            if (result.Count >= MaxTagsPerDevice) break;
        }

        return result;
    }

    // ---------------------------------------------------------------- internals

    private void ExecuteUpdate(string sql, string ipAddress, object value)
    {
        lock (_gate)
        {
            try
            {
                using var command = CreateCommand(sql);
                command.Parameters.AddWithValue("$value", value);
                command.Parameters.AddWithValue("$ip", ipAddress);
                command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Inventory update failed for {Ip}", ipAddress);
            }
        }
    }

    private StoredDevice? FindUnlocked(string ipAddress, SqliteTransaction? transaction = null)
    {
        using var command = CreateCommand("SELECT * FROM Devices WHERE IpAddress = $ip;", transaction);
        command.Parameters.AddWithValue("$ip", ipAddress);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    private SqliteCommand CreateCommand(string sql, SqliteTransaction? transaction = null)
    {
        if (_connection == null) throw new InvalidOperationException("The inventory database is not open.");

        var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        return command;
    }

    private void Execute(string sql)
    {
        using var command = CreateCommand(sql);
        command.ExecuteNonQuery();
    }

    private void CreateSchema()
    {
        Execute(
            """
            CREATE TABLE IF NOT EXISTS Devices (
                IpAddress         TEXT    NOT NULL PRIMARY KEY,
                Port              INTEGER NULL,
                Manufacturer      TEXT    NOT NULL DEFAULT '',
                Model             TEXT    NOT NULL DEFAULT '',
                MacAddress        TEXT    NULL,
                FirmwareVersion   TEXT    NULL,
                HardwareVersion   TEXT    NULL,
                SerialNumber      TEXT    NULL,
                Hostname          TEXT    NULL,
                DiscoveryProtocol TEXT    NOT NULL DEFAULT '',
                WebInterfaceUrl   TEXT    NULL,
                ApiEndpoint       TEXT    NULL,
                Username          TEXT    NULL,
                ProtectedPassword TEXT    NULL,
                Notes             TEXT    NULL,
                TagsCsv           TEXT    NOT NULL DEFAULT '',
                PropertiesJson    TEXT    NOT NULL DEFAULT '{}',
                FirstSeen         TEXT    NOT NULL,
                LastSeen          TEXT    NOT NULL
            );
            """);

        Execute("CREATE INDEX IF NOT EXISTS IX_Devices_Manufacturer ON Devices(Manufacturer);");
        Execute("CREATE INDEX IF NOT EXISTS IX_Devices_MacAddress ON Devices(MacAddress);");
        Execute("CREATE INDEX IF NOT EXISTS IX_Devices_LastSeen ON Devices(LastSeen);");

        var version = Convert.ToInt32(Scalar("PRAGMA user_version;") ?? 0, CultureInfo.InvariantCulture);
        if (version < SchemaVersion)
        {
            Execute($"PRAGMA user_version={SchemaVersion};");
            _logger.LogInformation("Inventory schema at version {Version}", SchemaVersion);
        }
    }

    private object? Scalar(string sql)
    {
        using var command = CreateCommand(sql);
        return command.ExecuteScalar();
    }

    /// <summary>Imports devices.json from the JSON era once, then parks the file next to the database.</summary>
    private void ImportLegacyJson()
    {
        if (!File.Exists(LegacyJsonPath)) return;

        try
        {
            var json = File.ReadAllText(LegacyJsonPath);
            var legacy = JsonSerializer.Deserialize<List<LegacyDevice>>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (legacy is { Count: > 0 })
            {
                var imported = 0;

                foreach (var item in legacy)
                {
                    if (string.IsNullOrWhiteSpace(item.IpAddress)) continue;
                    if (FindUnlocked(item.IpAddress) != null) continue;

                    using var command = CreateCommand(
                        """
                        INSERT INTO Devices
                            (IpAddress, Port, Manufacturer, Model, MacAddress, FirmwareVersion, HardwareVersion,
                             SerialNumber, Hostname, DiscoveryProtocol, WebInterfaceUrl, ApiEndpoint, Username,
                             ProtectedPassword, Notes, TagsCsv, PropertiesJson, FirstSeen, LastSeen)
                        VALUES
                            ($ip, $port, $manufacturer, $model, $mac, $firmware, $hardware,
                             $serial, $hostname, $protocol, $web, $api, $username,
                             $password, $notes, $tags, '{}', $firstSeen, $lastSeen);
                        """);

                    command.Parameters.AddWithValue("$ip", item.IpAddress);
                    command.Parameters.AddWithValue("$port", (object?)item.Port ?? DBNull.Value);
                    command.Parameters.AddWithValue("$manufacturer", item.Manufacturer ?? "");
                    command.Parameters.AddWithValue("$model", item.Model ?? "");
                    command.Parameters.AddWithValue("$mac", (object?)item.MacAddress ?? DBNull.Value);
                    command.Parameters.AddWithValue("$firmware", (object?)item.FirmwareVersion ?? DBNull.Value);
                    command.Parameters.AddWithValue("$hardware", (object?)item.HardwareVersion ?? DBNull.Value);
                    command.Parameters.AddWithValue("$serial", (object?)item.SerialNumber ?? DBNull.Value);
                    command.Parameters.AddWithValue("$hostname", (object?)item.Hostname ?? DBNull.Value);
                    command.Parameters.AddWithValue("$protocol", item.DiscoveryProtocol ?? "");
                    command.Parameters.AddWithValue("$web", (object?)item.WebInterfaceUrl ?? DBNull.Value);
                    command.Parameters.AddWithValue("$api", (object?)item.ApiEndpoint ?? DBNull.Value);
                    command.Parameters.AddWithValue("$username", (object?)item.Username ?? DBNull.Value);
                    command.Parameters.AddWithValue("$password", (object?)item.ProtectedPassword ?? DBNull.Value);
                    command.Parameters.AddWithValue("$notes", (object?)item.Notes ?? DBNull.Value);
                    command.Parameters.AddWithValue("$tags", JoinTags(item.Tags));
                    command.Parameters.AddWithValue("$firstSeen", item.FirstSeen == default ? DateTime.UtcNow : item.FirstSeen);
                    command.Parameters.AddWithValue("$lastSeen", item.LastSeen == default ? DateTime.UtcNow : item.LastSeen);

                    command.ExecuteNonQuery();
                    imported++;
                }

                var backup = $"{LegacyJsonPath}.imported";
                if (File.Exists(backup)) File.Delete(backup);
                File.Move(LegacyJsonPath, backup);

                _logger.LogInformation("Imported {Count} devices from {Path} into SQLite", imported, LegacyJsonPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Legacy JSON inventory at {Path} could not be imported", LegacyJsonPath);
        }
    }

    private static StoredDevice Map(SqliteDataReader reader)
    {
        T? Get<T>(string column) where T : class
            => reader[column] is DBNull ? null : reader[column] as T;

        return new StoredDevice
        {
            IpAddress = reader["IpAddress"] as string ?? "",
            Port = reader["Port"] is DBNull ? null : Convert.ToInt32(reader["Port"], CultureInfo.InvariantCulture),
            Manufacturer = reader["Manufacturer"] as string ?? "",
            Model = reader["Model"] as string ?? "",
            MacAddress = Get<string>("MacAddress"),
            FirmwareVersion = Get<string>("FirmwareVersion"),
            HardwareVersion = Get<string>("HardwareVersion"),
            SerialNumber = Get<string>("SerialNumber"),
            Hostname = Get<string>("Hostname"),
            DiscoveryProtocol = reader["DiscoveryProtocol"] as string ?? "",
            WebInterfaceUrl = Get<string>("WebInterfaceUrl"),
            ApiEndpoint = Get<string>("ApiEndpoint"),
            Username = Get<string>("Username"),
            ProtectedPassword = Get<string>("ProtectedPassword"),
            Notes = Get<string>("Notes"),
            Tags = SplitTags(reader["TagsCsv"] as string),
            FirstSeen = ParseDate(reader["FirstSeen"]),
            LastSeen = ParseDate(reader["LastSeen"])
        };
    }

    private static DateTime ParseDate(object value)
        => value is string text
           && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : DateTime.UtcNow;

    private static string JoinTags(IEnumerable<string>? tags)
        => string.Join(",", NormalizeTags(tags));

    private static List<string> SplitTags(string? csv)
        => string.IsNullOrWhiteSpace(csv) ? new List<string>() : NormalizeTags(csv.Split(','));

    private static string SerializeProperties(IDictionary<string, object>? properties)
    {
        if (properties == null || properties.Count == 0) return "{}";

        try
        {
            var simple = properties.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString() ?? "");
            return JsonSerializer.Serialize(simple);
        }
        catch
        {
            return "{}";
        }
    }

    /// <summary>Shape of the pre-SQLite devices.json entries.</summary>
    private sealed class LegacyDevice
    {
        public string IpAddress { get; set; } = "";
        public int? Port { get; set; }
        public string? Manufacturer { get; set; }
        public string? Model { get; set; }
        public string? MacAddress { get; set; }
        public string? FirmwareVersion { get; set; }
        public string? HardwareVersion { get; set; }
        public string? SerialNumber { get; set; }
        public string? Hostname { get; set; }
        public string? DiscoveryProtocol { get; set; }
        public string? WebInterfaceUrl { get; set; }
        public string? ApiEndpoint { get; set; }
        public string? Username { get; set; }
        public string? ProtectedPassword { get; set; }
        public string? Notes { get; set; }
        public List<string> Tags { get; set; } = new();
        public DateTime FirstSeen { get; set; }
        public DateTime LastSeen { get; set; }
    }
}