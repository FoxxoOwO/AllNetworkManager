using System.Net.Http;
using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using AllNetworkManager.App.Services;
using AllNetworkManager.App.ViewModels;
using AllNetworkManager.App.Views;
using AllNetworkManager.Core.Devices;
using AllNetworkManager.Core.Onvif;
using AllNetworkManager.Core.Vendors;
using AllNetworkManager.Discovery;
using AllNetworkManager.Vendors.Bosch;
using AllNetworkManager.Vendors.MikroTik;
using AllNetworkManager.Vendors.Wisenet;
using AllNetworkManager.Vendors.Microsens;
using AllNetworkManager.Vendors.Axis;
using AllNetworkManager.Vendors.Onvif;
using OnvifClient = AllNetworkManager.Vendors.Onvif.OnvifClient;

namespace AllNetworkManager.App;

public partial class App : Application
{
    private IHost? _host;

    public static IServiceProvider Services => ((App)Current)._host!.Services;

    protected override async void OnStartup(StartupEventArgs e)
    {
        _host = CreateHostBuilder().Build();
        await _host.StartAsync();

        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        mainWindow.Show();

        base.OnStartup(e);
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host != null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
        base.OnExit(e);
    }

    private static IHostBuilder CreateHostBuilder()
    {
        return Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration((context, config) =>
            {
                config.SetBasePath(AppContext.BaseDirectory);
                config.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
            })
            .ConfigureServices((context, services) =>
            {
                services.AddSingleton<MainWindow>();
                services.AddSingleton<MainViewModel>();
                services.AddSingleton<DeviceStore>();

                services.AddHttpClient();
                services.AddHttpClient(nameof(OnvifClient));
                services.AddHttpClient(nameof(BoschDeviceManager));

                // Per-device ONVIF client factory: each operation builds a client bound to that
                // device endpoint and credentials, so nothing is shared or cached wrongly.
                services.AddSingleton<Func<string, DeviceCredentials?, IOnvifClient>>(sp =>
                {
                    var factory = sp.GetRequiredService<IHttpClientFactory>();
                    var logger = sp.GetRequiredService<ILogger<OnvifClient>>();
                    return (address, creds) =>
                        new OnvifClient(logger, factory.CreateClient(nameof(OnvifClient)), address, creds);
                });

                services.AddDiscoveryServices(context.Configuration);

                // Vendor order matters: Bosch first, generic ONVIF last as the fallback.
                services.AddBoschSupport(context.Configuration["Bosch:SdkPath"]);
                services.AddMikroTikSupport();
                services.AddWisenetSupport();
                services.AddMicrosensSupport();
                services.AddAxisSupport();
                services.AddOnvifSupport();

                services.AddSingleton<IVendorRegistry>(sp =>
                    new VendorRegistry(sp.GetServices<IVendorDeviceManager>()));
            })
            .ConfigureLogging((context, logging) =>
            {
                logging.AddConfiguration(context.Configuration.GetSection("Logging"));
                logging.AddConsole();
                logging.AddDebug();
            });
    }
}
