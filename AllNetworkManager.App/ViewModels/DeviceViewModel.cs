using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AllNetworkManager.Core.Devices;
using AllNetworkManager.Core.Discovery;
using AllNetworkManager.Core.Vendors;
using AllNetworkManager.Core.Onvif;
using AllNetworkManager.App.Services;
using Microsoft.Extensions.Logging;

namespace AllNetworkManager.App.ViewModels;

public partial class DeviceViewModel : ObservableObject, IDisposable
{
    private readonly IVendorRegistry _vendors;
    private readonly ILogger _logger;
    private IVendorDeviceManager? _manager;

    [ObservableProperty] private DiscoveredDevice _device;
    [ObservableProperty] private DeviceStatus _status = DeviceStatus.Unknown;
    [ObservableProperty] private string _statusText = "Unknown";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _busyText = "";
    [ObservableProperty] private string _firmwareVersion = "";
    [ObservableProperty] private string _hardwareVersion = "";
    [ObservableProperty] private string _serialNumber = "";
    [ObservableProperty] private string _macAddress = "";
    [ObservableProperty] private string _hostname = "";
    [ObservableProperty] private string _vendorName = "";
    [ObservableProperty] private NetworkConfigViewModel? _networkConfig;
    [ObservableProperty] private ObservableCollection<UserAccountViewModel> _users = new();
    [ObservableProperty] private ObservableCollection<DeviceLogViewModel> _logs = new();
    [ObservableProperty] private OnvifCapabilities? _capabilities;
    [ObservableProperty] private string _username = "admin";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _newUserName = "";
    [ObservableProperty] private string _newUserPassword = "";
    [ObservableProperty] private string _newUserRole = "Operator";
    [ObservableProperty] private UserAccountViewModel? _selectedUser;
    [ObservableProperty] private string _notes = "";
    [ObservableProperty] private string _noteState = "";
    [ObservableProperty] private string _newTag = "";

    public string Id => Device.IpAddress;

    /// <summary>Second line of the device list: IP plus hostname when one is known.</summary>
    public string Subtitle => string.IsNullOrWhiteSpace(Hostname) ? IpAddress : $"{IpAddress}  ·  {Hostname}";

    public string HostnameDisplay => string.IsNullOrWhiteSpace(Hostname) ? "—" : Hostname;
    public string Name => $"{Device.Manufacturer} {Device.Model}".Trim();
    public string IpAddress => Device.IpAddress;
    public int? Port => Device.Port;
    public string Manufacturer => Device.Manufacturer;
    public string Model => Device.Model;
    public Uri? WebInterfaceUrl => Device.WebInterfaceUrl;
    public Uri? ApiEndpoint => Device.ApiEndpoint;
    public string DiscoveryProtocol => Device.DiscoveryProtocol;

    public DeviceCredentials Credentials => new(Username, Password);

    public bool CanFirmware => _manager is IVendorCapabilities c && c.SupportsFirmwareUpdate;
    public bool CanBackup => _manager is IVendorCapabilities c2 && c2.SupportsConfigBackupRestore;
    public bool CanRestore => _manager is IVendorCapabilities c2b && c2b.SupportsConfigRestore;
    public bool CanWake => AllNetworkManager.Core.Network.WakeOnLan.ParseMac(MacAddress) != null;
    public bool HasNotes => !string.IsNullOrWhiteSpace(Notes);
    public bool HasTags => Tags.Count > 0;
    public ObservableCollection<string> Tags { get; } = new();
    public string NotesTooltip => Notes;
    public bool CanUsers => _manager is IVendorCapabilities c3 && c3.SupportsUserManagement;
    public bool CanLogs => _manager is IVendorCapabilities c4 && c4.SupportsLogs;

    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand ApplyNetworkCommand { get; }
    public IAsyncRelayCommand LoadLogsCommand { get; }
    public IAsyncRelayCommand UploadFirmwareCommand { get; }
    public IAsyncRelayCommand BackupConfigCommand { get; }
    public IAsyncRelayCommand RestoreConfigCommand { get; }
    public IAsyncRelayCommand RebootCommand { get; }
    public IAsyncRelayCommand FactoryResetCommand { get; }
    public IAsyncRelayCommand AddUserCommand { get; }
    public IAsyncRelayCommand DeleteUserCommand { get; }
    public IAsyncRelayCommand SaveCredentialsCommand { get; }
    public IAsyncRelayCommand WakeCommand { get; }
    public IRelayCommand AddTagCommand { get; }
    public IRelayCommand<string> RemoveTagCommand { get; }

    /// <summary>Called with (ipAddress, hostname) when a hostname is learned. Wired by MainViewModel.</summary>
    public Action<string, string>? HostnameSink { get; set; }

    public Func<string, string, string, Task<bool>>? CredentialSink { get; set; }

    /// <summary>Called with (ipAddress, notes) after the user stops typing. Wired by MainViewModel.</summary>
    public Action<string, string?>? NoteSink { get; set; }

    /// <summary>Called with (ipAddress, tags) after every tag change. Wired by MainViewModel.</summary>
    public Action<string, List<string>>? TagSink { get; set; }

    private bool _loadingNotes;
    private string? _pendingNote;

    public DeviceViewModel(
        DiscoveredDevice device,
        IVendorRegistry vendors,
        ILogger logger,
        StoredDevice? stored = null)
    {
        _device = device;
        _vendors = vendors;
        _logger = logger;

        if (stored != null)
        {
            Username = stored.Username ?? "admin";
            Password = CredentialProtector.Unprotect(stored.ProtectedPassword);

            _loadingNotes = true;
            Notes = stored.Notes ?? "";
            _loadingNotes = false;

            NoteState = string.IsNullOrWhiteSpace(Notes) ? "" : "Uloženo";

            foreach (var tag in Services.DeviceStore.NormalizeTags(stored.Tags))
                Tags.Add(tag);
        }

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        ApplyNetworkCommand = new AsyncRelayCommand(ApplyNetworkAsync);
        LoadLogsCommand = new AsyncRelayCommand(LoadLogsAsync);
        UploadFirmwareCommand = new AsyncRelayCommand(UploadFirmwareAsync);
        BackupConfigCommand = new AsyncRelayCommand(BackupConfigAsync);
        RestoreConfigCommand = new AsyncRelayCommand(RestoreConfigAsync);
        RebootCommand = new AsyncRelayCommand(RebootAsync);
        FactoryResetCommand = new AsyncRelayCommand(FactoryResetAsync);
        AddUserCommand = new AsyncRelayCommand(AddUserAsync);
        DeleteUserCommand = new AsyncRelayCommand(DeleteUserAsync);
        SaveCredentialsCommand = new AsyncRelayCommand(SaveCredentialsAsync);
        WakeCommand = new AsyncRelayCommand(WakeAsync);
        AddTagCommand = new RelayCommand(AddTag);
        RemoveTagCommand = new RelayCommand<string>(RemoveTag);

        MacAddress = device.MacAddress ?? "";
        Hostname = stored?.Hostname is { Length: > 0 } storedHost
            ? storedHost
            : Services.HostnameResolver.FromProperties(device.Properties) ?? "";
        FirmwareVersion = device.FirmwareVersion ?? "";
        HardwareVersion = device.HardwareVersion ?? "";
        SerialNumber = device.SerialNumber ?? "";

        _manager = _vendors.Resolve(new DeviceWrapper(device));
        VendorName = _manager?.VendorName ?? "Unknown";
    }

    private void AddTag()
    {
        var candidate = (NewTag ?? "").Trim();
        if (candidate.Length == 0) return;

        if (Tags.Any(t => t.Equals(candidate, StringComparison.OrdinalIgnoreCase)))
        {
            NewTag = "";
            return;
        }

        if (Tags.Count >= Services.DeviceStore.MaxTagsPerDevice)
        {
            StatusText = $"Víc než {Services.DeviceStore.MaxTagsPerDevice} štítků nedává smysl";
            return;
        }

        Tags.Add(candidate.Length > Services.DeviceStore.MaxTagLength
            ? candidate[..Services.DeviceStore.MaxTagLength]
            : candidate);
        NewTag = "";
        PersistTags();
    }

    private void RemoveTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return;
        var existing = Tags.FirstOrDefault(t => t.Equals(tag, StringComparison.OrdinalIgnoreCase));
        if (existing == null) return;

        Tags.Remove(existing);
        PersistTags();
    }

    private void PersistTags()
    {
        OnPropertyChanged(nameof(HasTags));

        try
        {
            TagSink?.Invoke(IpAddress, Tags.ToList());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Štítky pro {Ip} se nepodařilo uložit", IpAddress);
            StatusText = "Štítky se nepodařilo uložit";
        }
    }

    partial void OnDeviceChanged(DiscoveredDevice value)
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(IpAddress));
        OnPropertyChanged(nameof(Port));
        OnPropertyChanged(nameof(Manufacturer));
        OnPropertyChanged(nameof(Model));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(WebInterfaceUrl));
        OnPropertyChanged(nameof(ApiEndpoint));
        OnPropertyChanged(nameof(DiscoveryProtocol));
        OnPropertyChanged(nameof(HostnameDisplay));
    }

    partial void OnHostnameChanged(string value)
    {
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(HostnameDisplay));

        if (string.IsNullOrWhiteSpace(value)) return;

        try
        {
            HostnameSink?.Invoke(IpAddress, value);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Hostname pro {Ip} se nepodařilo uložit", IpAddress);
        }
    }

    private CancellationTokenSource? _noteDebounce;

    partial void OnNotesChanged(string value)
    {
        if (_loadingNotes) return;

        OnPropertyChanged(nameof(HasNotes));
        OnPropertyChanged(nameof(NotesTooltip));

        _pendingNote = value;
        _noteDebounce?.Cancel();
        _noteDebounce = new CancellationTokenSource();
        var token = _noteDebounce.Token;
        SetNoteState("Neuloženo…");

        _ = Task.Run(async () =>
        {
            try { await Task.Delay(600, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            if (token.IsCancellationRequested) return;

            try
            {
                NoteSink?.Invoke(IpAddress, value);
                _pendingNote = null;
                SetNoteState($"Uloženo {DateTime.Now:HH:mm:ss}");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Poznámku pro {Ip} se nepodařilo uložit", IpAddress);
                SetNoteState("Uložení selhalo");
            }
        }, token);
    }

    /// <summary>Writes a note that is still waiting for the debounce timer (called when the window closes).</summary>
    public void FlushNotes()
    {
        var pending = _pendingNote;
        if (pending == null) return;

        _pendingNote = null;
        _noteDebounce?.Cancel();

        try
        {
            NoteSink?.Invoke(IpAddress, pending);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Poznámku pro {Ip} se nepodařilo uložit při zavírání", IpAddress);
        }
    }

    private void SetNoteState(string text)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
            dispatcher.Invoke(() => NoteState = text);
        else
            NoteState = text;
    }

    private async Task RefreshAsync()
    {
        await RunAsync("Nacitam zarizeni...", async () =>
        {
            _manager ??= await _vendors.ResolveAsync(new DeviceWrapper(Device));
            if (_manager == null) throw new InvalidOperationException("Pro toto zarizeni neni dostupna zadna vendor vrstva.");

            var info = await _manager.GetDeviceInfoAsync(new DeviceWrapper(Device), Credentials);
            FirmwareVersion = info.FirmwareVersion;
            HardwareVersion = info.HardwareVersion;
            SerialNumber = info.SerialNumber;
            MacAddress = info.MacAddress;
            VendorName = _manager.VendorName;

            var reported = Services.HostnameResolver.FromProperties(info.AdditionalInfo)
                           ?? Services.HostnameResolver.FromProperties(Device.Properties);
            var resolved = reported ?? await Services.HostnameResolver.ReverseDnsAsync(IpAddress);
            if (!string.IsNullOrWhiteSpace(resolved) && !string.Equals(resolved, Hostname, StringComparison.OrdinalIgnoreCase))
                Hostname = resolved;

            if (info.AdditionalInfo.TryGetValue("capabilities", out var capsObj) && capsObj is OnvifCapabilities caps)
                Capabilities = caps;

            // Reading the current network settings is only possible over ONVIF. Vendors such as MikroTik or
            // MICROSENS do not answer ONVIF at all, so this used to throw and mark the whole refresh as failed.
            if (_manager is IVendorCapabilities nc && nc.SupportsNetworkConfig && SpeaksOnvif)
                await LoadNetworkOverOnvifAsync();

            if (_manager is IVendorCapabilities uc && uc.SupportsUserManagement)
                await LoadUsersCoreAsync();

            Status = DeviceStatus.Online;
            StatusText = "Online";
            RaiseCapabilities();
        });
    }

    private void RaiseCapabilities()
    {
        OnPropertyChanged(nameof(CanFirmware));
        OnPropertyChanged(nameof(CanBackup));
        OnPropertyChanged(nameof(CanRestore));
        OnPropertyChanged(nameof(CanWake));
        OnPropertyChanged(nameof(CanUsers));
        OnPropertyChanged(nameof(CanLogs));
    }

    private IOnvifClient CreateOnvifClient() => new OnvifClientProxy(Endpoint(), Credentials);

    /// <summary>True for vendor layers whose native protocol is ONVIF (Bosch cameras, generic ONVIF).</summary>
    private bool SpeaksOnvif =>
        VendorName.Contains("ONVIF", StringComparison.OrdinalIgnoreCase)
        || VendorName.Contains("Bosch", StringComparison.OrdinalIgnoreCase);

    private string Endpoint()
    {
        if (Device.ApiEndpoint is { } api && api.Scheme.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return api.ToString();

        // https-only devices (Axis: 443) must not be probed over plain http
        var scheme = Device.Port == 443 ? "https" : "http";
        var port = Device.Port is > 0 and not 80 and not 443 ? $":{Device.Port}" : "";
        return $"{scheme}://{Device.IpAddress}{port}/onvif/device_service";
    }

    private async Task LoadNetworkOverOnvifAsync()
    {
        try
        {
            var ifaces = await CreateOnvifClient().GetNetworkInterfacesAsync();
            var iface = ifaces.Interfaces.FirstOrDefault();
            if (iface?.Ipv4 == null) return;

            NetworkConfig = new NetworkConfigViewModel
            {
                IpAddress = iface.Ipv4.Address ?? "",
                SubnetMask = PrefixToMask(iface.Ipv4.PrefixLength),
                Gateway = iface.Ipv4.Gateway ?? "",
                Dns1 = iface.Ipv4.DnsServers?.FirstOrDefault() ?? "",
                Dns2 = iface.Ipv4.DnsServers?.Skip(1).FirstOrDefault() ?? "",
                IsDhcp = iface.Ipv4.Mode == OnvifIpConfigMode.DHCP
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Aktuální síťové nastavení {Ip} se nepodařilo přečíst přes ONVIF", IpAddress);
            NetworkConfig = new NetworkConfigViewModel
            {
                IpAddress = IpAddress,
                SubnetMask = "",
                Gateway = "",
                Dns1 = "",
                Dns2 = "",
                IsDhcp = false
            };
        }
    }

    /// <summary>Updates the list values after a rescan without touching credentials, notes or tags.</summary>
    public void ApplyDiscovered(DiscoveredDevice device)
    {
        // later operations (backup, firmware, users) must see the fresh record, not the first-scan one
        Device = device;

        if (!string.IsNullOrWhiteSpace(device.FirmwareVersion) && device.FirmwareVersion != FirmwareVersion)
            FirmwareVersion = device.FirmwareVersion;

        if (!string.IsNullOrWhiteSpace(device.MacAddress) && device.MacAddress != MacAddress)
            MacAddress = device.MacAddress;

        if (!string.IsNullOrWhiteSpace(device.SerialNumber) && device.SerialNumber != SerialNumber)
            SerialNumber = device.SerialNumber;

        if (string.IsNullOrWhiteSpace(Hostname))
        {
            var reported = Services.HostnameResolver.FromProperties(device.Properties);
            if (!string.IsNullOrWhiteSpace(reported)) Hostname = reported;
        }
    }

    private async Task ApplyNetworkAsync()
    {
        if (NetworkConfig == null)
        {
            StatusText = "Nejprve nacti konfiguraci zarizeni.";
            return;
        }

        // a typo here re-addresses a device into oblivion, so validate before touching it
        if (!Services.NetworkConfigValidator.TryValidate(NetworkConfig.ToNetworkConfig(), out var problem))
        {
            StatusText = problem;
            return;
        }

        await RunAsync("Zapisuji sitovou konfiguraci...", async () =>
        {
            _manager ??= _vendors.Resolve(new DeviceWrapper(Device));
            if (_manager == null) throw new InvalidOperationException("Vendor vrstva neni dostupna.");

            var ok = await _manager.SetNetworkConfigAsync(new DeviceWrapper(Device), Credentials, NetworkConfig.ToNetworkConfig());
            StatusText = ok ? "Sitova konfigurace zapsana" : "Zarizeni konfiguraci odmitlo";
        });
    }

    private async Task LoadUsersCoreAsync()
    {
        if (_manager == null) return;
        var users = await _manager.GetUsersAsync(new DeviceWrapper(Device), Credentials);
        Users.Clear();
        foreach (var u in users)
            Users.Add(new UserAccountViewModel { Username = u.Username, Role = u.Role.ToString(), IsEnabled = u.IsEnabled });
    }

    private async Task AddUserAsync()
    {
        if (string.IsNullOrWhiteSpace(NewUserName))
        {
            StatusText = "Zadej uzivatelske jmeno.";
            return;
        }

        await RunAsync($"Vytvarim uzivatele {NewUserName}...", async () =>
        {
            _manager ??= _vendors.Resolve(new DeviceWrapper(Device));
            if (_manager == null) throw new InvalidOperationException("Vendor vrstva neni dostupna.");

            var role = Enum.TryParse<UserRole>(NewUserRole, true, out var r) ? r : UserRole.Operator;
            await _manager.SetUserAsync(new DeviceWrapper(Device), Credentials,
                new UserAccount(NewUserName, NewUserPassword, role, true, null, null, new Dictionary<string, object>()));

            await LoadUsersCoreAsync();
            NewUserName = "";
            NewUserPassword = "";
            StatusText = "Uzivatel vytvoren";
        });
    }

    private async Task DeleteUserAsync()
    {
        if (SelectedUser == null)
        {
            StatusText = "Vyber uzivatele v tabulce.";
            return;
        }

        var name = SelectedUser.Username;
        await RunAsync($"Mazu uzivatele {name}...", async () =>
        {
            _manager ??= _vendors.Resolve(new DeviceWrapper(Device));
            if (_manager == null) throw new InvalidOperationException("Vendor vrstva neni dostupna.");

            var ok = await _manager.DeleteUserAsync(new DeviceWrapper(Device), Credentials, name);
            await LoadUsersCoreAsync();
            StatusText = ok ? "Uzivatel smazan" : "Smazani selhalo";
        });
    }

    private async Task LoadLogsAsync()
    {
        await RunAsync("Nacitam logy...", async () =>
        {
            _manager ??= _vendors.Resolve(new DeviceWrapper(Device));
            if (_manager == null) throw new InvalidOperationException("Vendor vrstva neni dostupna.");

            var logs = await _manager.GetLogsAsync(new DeviceWrapper(Device), Credentials,
                new LogFilter(null, null, null, null, null, 2000));

            Logs.Clear();
            foreach (var l in logs)
                Logs.Add(new DeviceLogViewModel
                {
                    Timestamp = l.Timestamp,
                    Level = l.Level.ToString(),
                    Source = l.Source,
                    Message = l.Message
                });

            StatusText = Logs.Count == 0 ? "Zarizeni nevraci logy" : $"Nacteno {Logs.Count} zaznamu";
        });
    }

    private async Task UploadFirmwareAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Vyber firmware",
            Filter = "Firmware (*.bin;*.fw;*.img)|*.bin;*.fw;*.img|Vsechny soubory (*.*)|*.*"
        };
        if (dialog.ShowDialog() != true) return;

        var path = dialog.FileName;
        if (MessageBox.Show(
                $"Nahrat firmware {Path.GetFileName(path)} do {IpAddress}? Zarizeni se restartuje.",
                "Firmware", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;

        await RunAsync("Nahiram firmware...", async () =>
        {
            _manager ??= _vendors.Resolve(new DeviceWrapper(Device));
            if (_manager == null) throw new InvalidOperationException("Vendor vrstva neni dostupna.");

            var data = await File.ReadAllBytesAsync(path);
            var image = new FirmwareImage(
                Version: Path.GetFileNameWithoutExtension(path),
                FileName: Path.GetFileName(path),
                Data: data,
                ReleaseNotes: null,
                ReleaseDate: DateTime.UtcNow,
                IsBeta: false,
                CompatibleModels: new[] { Model });

            await _manager.UpdateFirmwareAsync(new DeviceWrapper(Device), Credentials, image);
            StatusText = "Firmware nahran, zarizeni se restartuje";
        });
    }

    private async Task BackupConfigAsync()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Ulozit zalohu konfigurace",
            FileName = $"{IpAddress.Replace('.', '-')}-{DateTime.Now:yyyyMMdd-HHmm}.cfg",
            Filter = "Zaloha (*.cfg)|*.cfg|Vsechny soubory (*.*)|*.*"
        };
        if (dialog.ShowDialog() != true) return;

        var path = dialog.FileName;
        await RunAsync("Zalohuji konfiguraci...", async () =>
        {
            _manager ??= _vendors.Resolve(new DeviceWrapper(Device));
            if (_manager == null) throw new InvalidOperationException("Vendor vrstva neni dostupna.");

            var backup = await _manager.BackupConfigurationAsync(new DeviceWrapper(Device), Credentials);
            await File.WriteAllBytesAsync(path, backup.Data);
            StatusText = $"Zaloha ulozena ({backup.Data.Length} B)";
        });
    }

    private async Task RestoreConfigAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Vyber zalohu konfigurace",
            Filter = "Zaloha (*.cfg;*.bin)|*.cfg;*.bin|Vsechny soubory (*.*)|*.*"
        };
        if (dialog.ShowDialog() != true) return;

        var path = dialog.FileName;
        if (MessageBox.Show(
                $"Obnovit konfiguraci z {Path.GetFileName(path)} do {IpAddress}? Zarizeni se restartuje.",
                "Obnova konfigurace", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;

        await RunAsync("Obnovuji konfiguraci...", async () =>
        {
            _manager ??= _vendors.Resolve(new DeviceWrapper(Device));
            if (_manager == null) throw new InvalidOperationException("Vendor vrstva neni dostupna.");

            var data = await File.ReadAllBytesAsync(path);
            var backup = new ConfigurationBackup(Id, Model, FirmwareVersion, DateTime.UtcNow, data, "raw");
            await _manager.RestoreConfigurationAsync(new DeviceWrapper(Device), Credentials, backup);
            StatusText = "Konfigurace obnovena";
        });
    }

    private async Task RebootAsync()
    {
        if (MessageBox.Show($"Restartovat {IpAddress}?", "Restart", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        await RunAsync("Restartuji...", async () =>
        {
            _manager ??= _vendors.Resolve(new DeviceWrapper(Device));
            if (_manager == null) throw new InvalidOperationException("Vendor vrstva neni dostupna.");

            await _manager.RebootAsync(new DeviceWrapper(Device), Credentials);
            StatusText = "Restart odeslan";
        });
    }

    private async Task FactoryResetAsync()
    {
        if (MessageBox.Show(
                $"Tovarni nastaveni {IpAddress}? Smazana bude veskerá konfigurace.",
                "Tovarni nastaveni", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;

        await RunAsync("Provadim tovarni nastaveni...", async () =>
        {
            _manager ??= _vendors.Resolve(new DeviceWrapper(Device));
            if (_manager == null) throw new InvalidOperationException("Vendor vrstva neni dostupna.");

            await _manager.FactoryResetAsync(new DeviceWrapper(Device), Credentials);
            StatusText = "Tovarni nastaveni odeslano";
        });
    }

    private async Task WakeAsync()
    {
        await RunAsync("Posilam Wake-on-LAN...", async () =>
        {
            var ok = await AllNetworkManager.Core.Network.WakeOnLan.SendAsync(MacAddress);
            StatusText = ok ? "Magic packet odeslan" : "WoL se nepodarilo odeslat";
        });
    }

    private async Task SaveCredentialsAsync()
    {
        if (CredentialSink != null)
            await CredentialSink(IpAddress, Username, Password);
        StatusText = "Prihlasovaci udaje ulozeny (DPAPI)";
    }

    private async Task RunAsync(string busyText, Func<Task> work)
    {
        if (IsBusy) return;

        IsBusy = true;
        BusyText = busyText;
        try
        {
            await work();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Device operation failed for {Ip}", IpAddress);
            Status = DeviceStatus.Error;
            StatusText = ex.Message.Length > 160 ? ex.Message[..160] : ex.Message;
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
        }
    }

    private static string PrefixToMask(string? prefix)
    {
        if (!int.TryParse(prefix, out var p) || p < 0 || p > 32) return "255.255.255.0";
        uint mask = p == 0 ? 0 : uint.MaxValue << (32 - p);
        return $"{(mask >> 24) & 0xFF}.{(mask >> 16) & 0xFF}.{(mask >> 8) & 0xFF}.{mask & 0xFF}";
    }

    public void Dispose()
    {
        _noteDebounce?.Cancel();
        _noteDebounce?.Dispose();
    }
}

internal class OnvifClientProxy : IOnvifClient
{
    private readonly AllNetworkManager.Vendors.Onvif.OnvifClient _inner;

    public OnvifClientProxy(string address, DeviceCredentials credentials)
        => _inner = new AllNetworkManager.Vendors.Onvif.OnvifClient(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AllNetworkManager.Vendors.Onvif.OnvifClient>.Instance,
            new HttpClient { Timeout = TimeSpan.FromSeconds(20) }, address, credentials);

    public string DeviceAddress => _inner.DeviceAddress;
    public Task<OnvifDeviceInfo> GetDeviceInfoAsync(CancellationToken ct = default) => _inner.GetDeviceInfoAsync(ct);
    public Task<OnvifNetworkInterfaces> GetNetworkInterfacesAsync(CancellationToken ct = default) => _inner.GetNetworkInterfacesAsync(ct);
    public Task<bool> SetNetworkInterfacesAsync(OnvifNetworkInterfaces config, CancellationToken ct = default) => _inner.SetNetworkInterfacesAsync(config, ct);
    public Task<SystemDateTime> GetSystemDateTimeAsync(CancellationToken ct = default) => _inner.GetSystemDateTimeAsync(ct);
    public Task<bool> SetSystemDateTimeAsync(SystemDateTime dateTime, CancellationToken ct = default) => _inner.SetSystemDateTimeAsync(dateTime, ct);
    public Task<OnvifUsers> GetUsersAsync(CancellationToken ct = default) => _inner.GetUsersAsync(ct);
    public Task<bool> CreateUserAsync(OnvifUser user, CancellationToken ct = default) => _inner.CreateUserAsync(user, ct);
    public Task<bool> DeleteUserAsync(string username, CancellationToken ct = default) => _inner.DeleteUserAsync(username, ct);
    public Task<bool> SetUserPasswordAsync(string username, string password, CancellationToken ct = default) => _inner.SetUserPasswordAsync(username, password, ct);
    public Task<OnvifCapabilities> GetCapabilitiesAsync(CancellationToken ct = default) => _inner.GetCapabilitiesAsync(ct);
    public Task RebootAsync(CancellationToken ct = default) => _inner.RebootAsync(ct);
    public Task FactoryResetAsync(CancellationToken ct = default) => _inner.FactoryResetAsync(ct);
    public Task<Stream> GetSnapshotUriAsync(string profileToken, CancellationToken ct = default) => _inner.GetSnapshotUriAsync(profileToken, ct);
    public Task<Uri> GetStreamUriAsync(string profileToken, StreamType streamType, CancellationToken ct = default) => _inner.GetStreamUriAsync(profileToken, streamType, ct);
    public Task<PtzStatus> GetPtzStatusAsync(string profileToken, CancellationToken ct = default) => _inner.GetPtzStatusAsync(profileToken, ct);
    public Task<bool> PtzMoveAsync(string profileToken, PtzVector vector, CancellationToken ct = default) => _inner.PtzMoveAsync(profileToken, vector, ct);
    public Task<bool> PtzStopAsync(string profileToken, CancellationToken ct = default) => _inner.PtzStopAsync(profileToken, ct);
    public Task<OnvifRecordingConfig> GetRecordingConfigAsync(CancellationToken ct = default) => _inner.GetRecordingConfigAsync(ct);
    public Task<OnvifAnalyticsConfig> GetAnalyticsConfigAsync(CancellationToken ct = default) => _inner.GetAnalyticsConfigAsync(ct);
}

public partial class NetworkConfigViewModel : ObservableObject
{
    [ObservableProperty] public string _ipAddress = "";
    [ObservableProperty] public string _subnetMask = "255.255.255.0";
    [ObservableProperty] public string _gateway = "";
    [ObservableProperty] public string _dns1 = "";
    [ObservableProperty] public string _dns2 = "";
    [ObservableProperty] public bool _isDhcp = true;
    [ObservableProperty] public int? _vlanId;
    [ObservableProperty] public string _hostname = "";

    public NetworkConfig ToNetworkConfig() => new(
        IpAddress, SubnetMask, Gateway, Dns1, Dns2, IsDhcp, VlanId, Hostname);
}

public partial class UserAccountViewModel : ObservableObject
{
    [ObservableProperty] public string _username = "";
    [ObservableProperty] public string _role = "";
    [ObservableProperty] public bool _isEnabled;
}

public partial class DeviceLogViewModel : ObservableObject
{
    [ObservableProperty] public DateTime _timestamp;
    [ObservableProperty] public string _level = "";
    [ObservableProperty] public string _source = "";
    [ObservableProperty] public string _message = "";
}

internal class DeviceWrapper : IDevice
{
    private readonly DiscoveredDevice _device;
    public DeviceWrapper(DiscoveredDevice device) => _device = device;

    public string Id => _device.IpAddress;
    public string Name => $"{_device.Manufacturer} {_device.Model}".Trim();
    public string IpAddress => _device.IpAddress;
    public int? Port => _device.Port;
    public string MacAddress => _device.MacAddress ?? "";
    public string Manufacturer => _device.Manufacturer;
    public string Model => _device.Model;
    public string FirmwareVersion => _device.FirmwareVersion ?? "";
    public string HardwareVersion => _device.HardwareVersion ?? "";
    public string SerialNumber => _device.SerialNumber ?? "";
    public DeviceType Type => _device.Type;
    public DeviceStatus Status => DeviceStatus.Unknown;
    public DateTime DiscoveredAt => DateTime.UtcNow;
    public DateTime LastSeen => DateTime.UtcNow;
    public Dictionary<string, object> Properties => _device.Properties ?? new();
    public IEnumerable<AllNetworkManager.Core.Devices.NetworkInterface> NetworkInterfaces => Array.Empty<AllNetworkManager.Core.Devices.NetworkInterface>();
    public Uri? WebInterfaceUrl => _device.WebInterfaceUrl;
    public Uri? ApiEndpoint => _device.ApiEndpoint;
}
