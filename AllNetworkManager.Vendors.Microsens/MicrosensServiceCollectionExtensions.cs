namespace AllNetworkManager.Vendors.Microsens;

using Microsoft.Extensions.DependencyInjection;
using AllNetworkManager.Core.Vendors;
using AllNetworkManager.Core.Discovery;

public static class MicrosensServiceCollectionExtensions
{
    /// <summary>Registers the MICROSENS switch layer (proprietary UDP protocol on 8340/1025).</summary>
    public static IServiceCollection AddMicrosensSupport(this IServiceCollection services)
    {
        // Discovery protocols live here (not in the Discovery assembly) to avoid a project cycle:
        // the framing codec belongs to the vendor layer, the pipeline just consumes IDiscoveryProtocol.
        services.AddSingleton<IDiscoveryProtocol, MicrosensDiscoveryProtocol>();
        services.AddSingleton<MicrosensDeviceManager>();
        services.AddSingleton<IVendorDeviceManager>(sp => sp.GetRequiredService<MicrosensDeviceManager>());
        services.AddSingleton<IVendorCapabilities>(sp => sp.GetRequiredService<MicrosensDeviceManager>());
        return services;
    }
}
