using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AllNetworkManager.Core.Devices;
using AllNetworkManager.Core.Discovery;
using AllNetworkManager.Core.Vendors;
using AllNetworkManager.App.Services;
using Microsoft.Extensions.Logging;

namespace AllNetworkManager.App.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly IDiscoveryService _discoveryService;
    private readonly IVendorRegistry _vendors;
    private readonly DeviceStore _store;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<MainViewModel> _logger;
    private readonly DispatcherTimer _rescanTimer;
    private CancellationTokenSource? _scanCts;

    [ObservableProperty] private bool _isDiscovering;
    [ObservableProperty] private string _discoveryStatus = "Pripraveno";
    [ObservableProperty] private double _discoveryProgress;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _showOnlyOnline;
    [ObservableProperty] private bool _autoRescan = true;
    [ObservableProperty] private int _rescanIntervalSeconds = 120;
    [ObservableProperty] private DeviceViewModel? _selectedDevice;
    [ObservableProperty] private string _storePath = "";

    public ObservableCollection<DeviceViewModel> Devices { get; } = new();
    public ICollectionView FilteredDevices { get; }

    public IAsyncRelayCommand DiscoverCommand { get; }
    public IRelayCommand StopDiscoveryCommand { get; }
    public IAsyncRelayCommand RefreshDeviceCommand { get; }
    public IAsyncRelayCommand OpenWebInterfaceCommand { get; }
    public IAsyncRelayCommand PingCommand { get; }
    public IRelayCommand ExportCsvCommand { get; }

    public MainViewModel(
        IDiscoveryService discoveryService,
        IVendorRegistry vendors,
        DeviceStore store,
        ILoggerFactory loggerFactory,
        ILogger<MainViewModel> logger)
    {
        _discoveryService = discoveryService;
        _vendors = vendors;
        _store = store;
        _loggerFactory = loggerFactory;
        _logger = logger;

        _discoveryService.DeviceDiscovered += OnDeviceDiscovered;
        _discoveryService.ProgressChanged += OnProgressChanged;

        FilteredDevices = CollectionViewSource.GetDefaultView(Devices);
        FilteredDevices.Filter = FilterDevice;

        DiscoverCommand = new AsyncRelayCommand(DiscoverAsync, () => !IsDiscovering);
        StopDiscoveryCommand = new RelayCommand(StopDiscovery, () => IsDiscovering);
        RefreshDeviceCommand = new AsyncRelayCommand(RefreshSelectedAsync, () => SelectedDevice != null);
        OpenWebInterfaceCommand = new AsyncRelayCommand(OpenWebInterfaceAsync, () => SelectedDevice?.WebInterfaceUrl != null);
        PingCommand = new AsyncRelayCommand(PingSelectedAsync, () => SelectedDevice != null);
        ExportCsvCommand = new RelayCommand(ExportCsv);

        _store.Load();
        StorePath = _store.FilePath;
        SeedFromStore();

        _rescanTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Max(30, RescanIntervalSeconds)) };
        _rescanTimer.Tick += async (_, _) => await SilentRescanAsync();
        if (AutoRescan) _rescanTimer.Start();
    }

    private void SeedFromStore()
    {
        Devices.Clear();
        foreach (var stored in _store.Snapshot().OrderBy(d => d.IpAddress, StringComparer.OrdinalIgnoreCase))
            Devices.Add(CreateDeviceViewModel(stored.ToDiscovered(), stored));
    }

    private DeviceViewModel CreateDeviceViewModel(DiscoveredDevice discovered, StoredDevice? stored)
    {
        var vm = new DeviceViewModel(discovered, _vendors, _loggerFactory.CreateLogger<DeviceViewModel>(), stored);
        vm.CredentialSink = SaveCredentialsAsync;
        vm.NoteSink = SaveNotes;
        vm.TagSink = SaveTags;
        vm.HostnameSink = SaveHostname;
        return vm;
    }

    /// <summary>Persists notes still sitting in a debounce timer. Call before the window closes.</summary>
    public void FlushNotes()
    {
        foreach (var device in Devices) device.FlushNotes();
    }

    private void SaveHostname(string ip, string hostname)
    {
        _store.SetHostname(ip, hostname);
        _store.Save();
        RefreshFilter();
    }

    private void SaveTags(string ip, List<string> tags)
    {
        _store.SetTags(ip, tags);
        _store.Save();
        RefreshFilter();
    }

    private void SaveNotes(string ip, string? notes)
    {
        _store.SetNotes(ip, notes);
        _store.Save();
        RefreshFilter();
    }

    private Task<bool> SaveCredentialsAsync(string ip, string user, string password)
    {
        _store.SetCredentials(ip, user, password);
        _store.Save();
        return Task.FromResult(true);
    }

    private bool FilterDevice(object item)
    {
        if (item is not DeviceViewModel device) return false;

        if (ShowOnlyOnline && device.Status != DeviceStatus.Online) return false;
        if (string.IsNullOrWhiteSpace(SearchText)) return true;

        var q = SearchText.Trim();
        return device.IpAddress.Contains(q, StringComparison.OrdinalIgnoreCase)
            || device.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
            || device.Manufacturer.Contains(q, StringComparison.OrdinalIgnoreCase)
            || device.Model.Contains(q, StringComparison.OrdinalIgnoreCase)
            || device.SerialNumber.Contains(q, StringComparison.OrdinalIgnoreCase)
            || device.MacAddress.Contains(q, StringComparison.OrdinalIgnoreCase)
            || device.Notes.Contains(q, StringComparison.OrdinalIgnoreCase)
            || device.Tags.Any(t => t.Contains(q, StringComparison.OrdinalIgnoreCase));
    }

    private async Task DiscoverAsync()
    {
        if (IsDiscovering) return;

        IsDiscovering = true;
        DiscoveryStatus = "Hledam zarizeni...";
        DiscoveryProgress = 0;
        DiscoverCommand.NotifyCanExecuteChanged();
        StopDiscoveryCommand.NotifyCanExecuteChanged();

        try
        {
            _scanCts?.Dispose();
            _scanCts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var found = (await _discoveryService.DiscoverAsync(_scanCts.Token)).ToList();

            _store.UpsertRange(found);
            _store.Save();
            MergeIntoUi(found);

            DiscoveryStatus = $"Nalezeno {found.Count} zarizeni (ulozeno celkem {_store.Snapshot().Count})";
        }
        catch (OperationCanceledException)
        {
            DiscoveryStatus = "Hledani zruseno";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Discovery failed");
            DiscoveryStatus = $"Chyba: {ex.Message}";
        }
        finally
        {
            IsDiscovering = false;
            DiscoveryProgress = 1;
            DiscoverCommand.NotifyCanExecuteChanged();
            StopDiscoveryCommand.NotifyCanExecuteChanged();
        }
    }

    private async Task SilentRescanAsync()
    {
        if (IsDiscovering) return;

        IsDiscovering = true;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var found = (await _discoveryService.DiscoverAsync(cts.Token)).ToList();
            _store.UpsertRange(found);
            _store.Save();

            var added = MergeIntoUi(found);
            DiscoveryStatus = added > 0
                ? $"Auto-rescan: {found.Count} zarizeni, {added} novych"
                : $"Auto-rescan: {found.Count} zarizeni";
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Auto-rescan failed");
        }
        finally
        {
            IsDiscovering = false;
        }
    }

    /// <summary>Adds newly discovered devices to the list; returns how many were new.</summary>
    private int MergeIntoUi(IEnumerable<DiscoveredDevice> found)
    {
        int added = 0;

        foreach (var d in found)
        {
            var existing = Devices.FirstOrDefault(x => x.IpAddress == d.IpAddress);
            if (existing == null)
            {
                Devices.Add(CreateDeviceViewModel(d, _store.Find(d.IpAddress)));
                added++;
            }
            else
            {
                existing.ApplyDiscovered(d);
            }
        }

        FilteredDevices.Refresh();
        return added;
    }

    /// <summary>Runs work on the UI thread without blocking the caller (discovery runs on worker threads).</summary>
    private static void PostToUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
            return;

        dispatcher.BeginInvoke(DispatcherPriority.Background, action);
    }

    /// <summary>Search matches notes and tags, so the view has to re-filter when they change.</summary>
    private void RefreshFilter() => PostToUi(() => FilteredDevices.Refresh());

    private void StopDiscovery()
    {
        _scanCts?.Cancel();
        DiscoveryStatus = "Zastavuji...";
    }

    private void OnDeviceDiscovered(object? sender, DiscoveredDevice device)
    {
        PostToUi(() =>
        {
            _store.UpsertRange(new[] { device });
            if (MergeIntoUi(new[] { device }) > 0)
                _store.Save();
        });
    }

    private void OnProgressChanged(object? sender, DiscoveryProgress progress)
    {
        PostToUi(() =>
        {
            DiscoveryStatus = $"{progress.CurrentOperation}: {progress.CurrentSubnet} ({progress.DevicesFound} nalezeno)";
            if (progress.TotalSubnets > 0)
                DiscoveryProgress = (double)progress.SubnetsScanned / progress.TotalSubnets;
        });
    }

    private async Task RefreshSelectedAsync()
    {
        if (SelectedDevice == null) return;
        await SelectedDevice.RefreshCommand.ExecuteAsync(null);
        FilteredDevices.Refresh();
    }

    private async Task OpenWebInterfaceAsync()
    {
        var url = SelectedDevice?.WebInterfaceUrl;
        if (url == null) return;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url.ToString(),
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cannot open web interface");
        }
    }

    private async Task PingSelectedAsync()
    {
        if (SelectedDevice == null) return;

        try
        {
            using var ping = new System.Net.NetworkInformation.Ping();
            var reply = await ping.SendPingAsync(SelectedDevice.IpAddress, 2000);
            SelectedDevice.Status = reply.Status == System.Net.NetworkInformation.IPStatus.Success
                ? DeviceStatus.Online
                : DeviceStatus.Unreachable;
            FilteredDevices.Refresh();
        }
        catch
        {
            SelectedDevice.Status = DeviceStatus.Error;
        }
    }

    private void ExportCsv()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export inventare",
            FileName = $"inventory-{DateTime.Now:yyyyMMdd-HHmm}.csv",
            Filter = "CSV (*.csv)|*.csv"
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            WriteCsv(dialog.FileName);
            DiscoveryStatus = $"Export: {Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CSV export failed");
            DiscoveryStatus = $"Export selhal: {ex.Message}";
        }
    }

    private void WriteCsv(string file)
    {
        var sb = new StringBuilder();
        sb.AppendLine("IpAddress;Hostname;Manufacturer;Model;Firmware;Hardware;Serial;Mac;Protocol;Vendor;WebInterface;Tags;Notes;FirstSeen;LastSeen");

        foreach (var d in _store.Snapshot().OrderBy(x => x.IpAddress, StringComparer.OrdinalIgnoreCase))
        {
            var vm = Devices.FirstOrDefault(v => v.IpAddress == d.IpAddress);
            sb.AppendLine(string.Join(';',
                d.IpAddress,
                Csv(d.Hostname ?? vm?.Hostname ?? ""),
                Csv(d.Manufacturer),
                Csv(d.Model),
                Csv(d.FirmwareVersion ?? ""),
                Csv(d.HardwareVersion ?? ""),
                Csv(d.SerialNumber ?? ""),
                Csv(d.MacAddress ?? ""),
                Csv(d.DiscoveryProtocol),
                Csv(vm?.VendorName ?? ""),
                Csv(d.WebInterfaceUrl ?? ""),
                Csv(string.Join(", ", d.Tags)),
                Csv(d.Notes ?? ""),
                d.FirstSeen.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                d.LastSeen.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)));
        }

        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
    }

    private static string Csv(string value) =>
        value.Contains(';') || value.Contains('"')
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;

    partial void OnSearchTextChanged(string value) => FilteredDevices.Refresh();
    partial void OnShowOnlyOnlineChanged(bool value) => FilteredDevices.Refresh();

    partial void OnRescanIntervalSecondsChanged(int value)
    {
        // the old code only used this value when the timer was created, so changing it did nothing
        _rescanTimer.Interval = TimeSpan.FromSeconds(Math.Max(30, value));
    }

    partial void OnAutoRescanChanged(bool value)
    {
        if (value) _rescanTimer.Start();
        else _rescanTimer.Stop();
    }

    partial void OnSelectedDeviceChanged(DeviceViewModel? value)
    {
        RefreshDeviceCommand.NotifyCanExecuteChanged();
        OpenWebInterfaceCommand.NotifyCanExecuteChanged();
        PingCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        _rescanTimer.Stop();
        _scanCts?.Cancel();
        _scanCts?.Dispose();
        _discoveryService.DeviceDiscovered -= OnDeviceDiscovered;
        _discoveryService.ProgressChanged -= OnProgressChanged;
    }
}
