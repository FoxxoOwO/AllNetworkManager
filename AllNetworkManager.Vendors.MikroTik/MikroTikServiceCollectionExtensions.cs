namespace AllNetworkManager.Vendors.MikroTik;

using Microsoft.Extensions.DependencyInjection;
using AllNetworkManager.Core.Vendors;

public static class MikroTikServiceCollectionExtensions
{
    /// <summary>Registers the MikroTik RouterOS vendor layer (binary API 8728 / TLS 8729).</summary>
    public static IServiceCollection AddMikroTikSupport(this IServiceCollection services)
    {
        services.AddSingleton<MikroTikDeviceManager>();
        services.AddSingleton<IVendorDeviceManager>(sp => sp.GetRequiredService<MikroTikDeviceManager>());
        services.AddSingleton<IVendorCapabilities>(sp => sp.GetRequiredService<MikroTikDeviceManager>());
        return services;
    }
}
