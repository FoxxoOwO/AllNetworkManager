namespace AllNetworkManager.Vendors.Onvif;

using Microsoft.Extensions.DependencyInjection;
using AllNetworkManager.Core.Vendors;

public static class OnvifServiceCollectionExtensions
{
    /// <summary>
    /// Registers the generic ONVIF vendor layer. Register it last so vendor-specific layers
    /// (Bosch, Axis, Wisenet...) get the first chance at a device in the vendor registry.
    /// Requires a <c>Func&lt;string, DeviceCredentials?, IOnvifClient&gt;</c> factory in the container.
    /// </summary>
    public static IServiceCollection AddOnvifSupport(this IServiceCollection services)
    {
        services.AddSingleton<OnvifDeviceManager>();
        services.AddSingleton<IVendorDeviceManager>(sp => sp.GetRequiredService<OnvifDeviceManager>());
        services.AddSingleton<IVendorCapabilities>(sp => sp.GetRequiredService<OnvifDeviceManager>());
        return services;
    }
}
