namespace AllNetworkManager.Core.Vendors;

using AllNetworkManager.Core.Devices;

public interface IVendorRegistry
{
    IReadOnlyList<IVendorDeviceManager> Managers { get; }
    IVendorDeviceManager? Resolve(IDevice device);
    Task<IVendorDeviceManager?> ResolveAsync(IDevice device, CancellationToken ct = default);
}

/// <summary>
/// Picks the vendor layer for a device: manufacturer name match first (cheap, works on
/// discovered-but-unauthenticated devices), then a protocol probe, then the generic ONVIF layer.
/// </summary>
public class VendorRegistry : IVendorRegistry
{
    private readonly IReadOnlyList<IVendorDeviceManager> _managers;

    public VendorRegistry(IEnumerable<IVendorDeviceManager> managers)
        => _managers = managers.ToList();

    public IReadOnlyList<IVendorDeviceManager> Managers => _managers;

    public IVendorDeviceManager? Resolve(IDevice device)
    {
        var byName = _managers.FirstOrDefault(m =>
            !string.IsNullOrWhiteSpace(device.Manufacturer) &&
            device.Manufacturer.Contains(m.VendorName, StringComparison.OrdinalIgnoreCase));

        if (byName != null) return byName;

        var byModel = _managers.FirstOrDefault(m =>
            m.SupportedModels.Any(model =>
                !string.IsNullOrWhiteSpace(device.Model) &&
                device.Model.Contains(model, StringComparison.OrdinalIgnoreCase)));

        if (byModel != null) return byModel;

        return _managers.FirstOrDefault(m => m.VendorName.StartsWith("ONVIF", StringComparison.OrdinalIgnoreCase));
    }

    public async Task<IVendorDeviceManager?> ResolveAsync(IDevice device, CancellationToken ct = default)
    {
        var guess = Resolve(device);
        if (guess != null && !guess.VendorName.StartsWith("ONVIF", StringComparison.OrdinalIgnoreCase))
            return guess;

        foreach (var manager in _managers.Where(m => !m.VendorName.StartsWith("ONVIF", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(4));
                if (await manager.CanManageAsync(device)) return manager;
            }
            catch { }
        }

        return guess;
    }
}
