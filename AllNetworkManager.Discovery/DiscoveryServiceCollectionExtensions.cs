namespace AllNetworkManager.Discovery;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using AllNetworkManager.Core.Discovery;
using AllNetworkManager.Discovery.Protocols;
using AllNetworkManager.Discovery.Protocols.Mdns;

public static class DiscoveryServiceCollectionExtensions
{
    public static IServiceCollection AddDiscoveryServices(this IServiceCollection services, IConfiguration? configuration = null)
    {
        if (configuration != null)
        {
            services.Configure<DiscoveryOptions>(configuration.GetSection("Discovery").Bind);
        }
        else
        {
            services.Configure<DiscoveryOptions>(options =>
            {
                options.AutoDetectSubnets = true;
                options.EnableWsDiscovery = true;
                options.EnableSsdp = true;
                options.EnableMndp = true;
                options.EnablePortScan = false;
                options.ScanTimeoutSeconds = 10;
            });
        }

        services.AddSingleton<IDiscoveryProtocol, WsDiscoveryProtocol>();
        services.AddSingleton<IDiscoveryProtocol, SsdpProtocol>();
        services.AddSingleton<IDiscoveryProtocol, MndpProtocol>();

        // Vendor broadcast protocols (single datagram out, replies parsed per vendor). Ports follow the
        // UniversalScanner protocol matrix; each one is a separate class so a broken one cannot take down
        // discovery for the rest.
        services.AddSingleton<IDiscoveryProtocol, HikvisionDiscoveryProtocol>();
        services.AddSingleton<IDiscoveryProtocol, DahuaDhipDiscoveryProtocol>();
        services.AddSingleton<IDiscoveryProtocol, DahuaLegacyDiscoveryProtocol>();
        services.AddSingleton<IDiscoveryProtocol, VivotekDiscoveryProtocol>();
        services.AddSingleton<IDiscoveryProtocol, UbiquitiDiscoveryProtocol>();
        services.AddSingleton<IDiscoveryProtocol, HanwhaDiscoveryProtocol>();

        // text/XML based vendor protocols
        services.AddSingleton<IDiscoveryProtocol, MsSqlBrowserDiscoveryProtocol>();
        services.AddSingleton<IDiscoveryProtocol, MicrochipDiscoveryProtocol>();
        services.AddSingleton<IDiscoveryProtocol, Vision360DiscoveryProtocol>();
        services.AddSingleton<IDiscoveryProtocol, SonyDiscoveryProtocol>();
        services.AddSingleton<IDiscoveryProtocol, EatonDiscoveryProtocol>();
        services.AddSingleton<IDiscoveryProtocol, EdenOptimaDiscoveryProtocol>();

        // binary vendor protocols
        services.AddSingleton<IDiscoveryProtocol, LantronixDiscoveryProtocol>();
        services.AddSingleton<IDiscoveryProtocol, AdvantechDiscoveryProtocol>();
        services.AddSingleton<IDiscoveryProtocol, VstarcamDiscoveryProtocol>();
        services.AddSingleton<IDiscoveryProtocol, NiceVisionDiscoveryProtocol>();

        // DNS-SD over mDNS
        services.AddSingleton<IDiscoveryProtocol, AxisMdnsDiscoveryProtocol>();
        services.AddSingleton<IDiscoveryProtocol, ArecontMdnsDiscoveryProtocol>();
        services.AddSingleton<IDiscoveryProtocol, GoogleCastDiscoveryProtocol>();
        services.AddSingleton<IDiscoveryService, DiscoveryService>();

        return services;
    }
}