namespace AllNetworkManager.Vendors.Axis;

using Microsoft.Extensions.DependencyInjection;
using AllNetworkManager.Core.Vendors;

public static class AxisServiceCollectionExtensions
{
    /// <summary>Registers the Axis VAPIX vendor layer (HTTP/HTTPS + Digest).</summary>
    public static IServiceCollection AddAxisSupport(this IServiceCollection services)
    {
        services.AddSingleton<AxisDeviceManager>();
        services.AddSingleton<IVendorDeviceManager>(sp => sp.GetRequiredService<AxisDeviceManager>());
        services.AddSingleton<IVendorCapabilities>(sp => sp.GetRequiredService<AxisDeviceManager>());
        return services;
    }
}
