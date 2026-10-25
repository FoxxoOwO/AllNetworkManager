namespace AllNetworkManager.Vendors.Bosch;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AllNetworkManager.Core.Devices;
using AllNetworkManager.Core.Vendors;
using AllNetworkManager.Core.Onvif;

public static class BoschServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Bosch vendor layer. Requires a
    /// <c>Func&lt;string, DeviceCredentials?, IOnvifClient&gt;</c> factory in the container.
    /// </summary>
    public static IServiceCollection AddBoschSupport(this IServiceCollection services, string? sdkPath = null)
    {
        services.AddSingleton<BoschVideoSdkWrapper>(sp =>
            new BoschVideoSdkWrapper(sp.GetRequiredService<ILogger<BoschVideoSdkWrapper>>(), sdkPath));

        services.AddHttpClient(nameof(BoschDeviceManager));

        services.AddSingleton<BoschDeviceManager>(sp =>
            new BoschDeviceManager(
                sp.GetRequiredService<ILogger<BoschDeviceManager>>(),
                sp.GetRequiredService<Func<string, DeviceCredentials?, IOnvifClient>>(),
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(BoschDeviceManager)),
                sp.GetRequiredService<BoschVideoSdkWrapper>()));

        services.AddSingleton<IVendorDeviceManager>(sp => sp.GetRequiredService<BoschDeviceManager>());
        services.AddSingleton<IVendorCapabilities>(sp => sp.GetRequiredService<BoschDeviceManager>());

        return services;
    }
}
