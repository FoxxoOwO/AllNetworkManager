namespace AllNetworkManager.Vendors.Wisenet;

using Microsoft.Extensions.DependencyInjection;
using AllNetworkManager.Core.Vendors;

public static class WisenetServiceCollectionExtensions
{
    /// <summary>Registers the Hanwha/Samsung Wisenet vendor layer (SUNAPI over HTTP CGI).</summary>
    public static IServiceCollection AddWisenetSupport(this IServiceCollection services)
    {
        services.AddSingleton<WisenetDeviceManager>();
        services.AddSingleton<IVendorDeviceManager>(sp => sp.GetRequiredService<WisenetDeviceManager>());
        services.AddSingleton<IVendorCapabilities>(sp => sp.GetRequiredService<WisenetDeviceManager>());
        return services;
    }
}
