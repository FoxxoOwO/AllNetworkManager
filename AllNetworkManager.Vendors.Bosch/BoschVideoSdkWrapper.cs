namespace AllNetworkManager.Vendors.Bosch;

using AllNetworkManager.Core.Devices;
using AllNetworkManager.Core.Vendors;
using AllNetworkManager.Core.Exceptions;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Reflection;

public class BoschVideoSdkWrapper
{
    private readonly ILogger<BoschVideoSdkWrapper> _logger;
    private readonly string _sdkPath;
    private Assembly? _bvipAssembly;
    private Assembly? _coreAssembly;
    private object? _bvipClient;
    private bool _initialized = false;

    public BoschVideoSdkWrapper(ILogger<BoschVideoSdkWrapper> logger, string? sdkPath = null)
    {
        _logger = logger;
        _sdkPath = sdkPath ?? FindSdkPath();
    }

    private string FindSdkPath()
    {
        var basePaths = new[]
        {
            @"D:\repositories\AllNetworkManager\ConfigManager\bin",
            @"C:\Program Files\Bosch\VideoSDK",
            @"C:\Program Files (x86)\Bosch\VideoSDK",
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BoschSDK")
        };

        foreach (var path in basePaths)
        {
            if (Directory.Exists(path) && File.Exists(Path.Combine(path, "Bosch.VideoSDK5.BVIP.dll")))
                return path;
        }

        return basePaths[0];
    }

    public async Task InitializeAsync()
    {
        if (_initialized) return;

        try
        {
            _bvipAssembly = LoadAssembly("Bosch.VideoSDK5.BVIP");
            _coreAssembly = LoadAssembly("Bosch.VideoSDK5.Core");

            if (_bvipAssembly != null)
            {
                var clientType = _bvipAssembly.GetType("Bosch.VideoSDK5.BVIP.BvipClient");
                if (clientType != null)
                {
                    _bvipClient = Activator.CreateInstance(clientType);
                    _initialized = true;
                    _logger.LogInformation("Bosch VideoSDK initialized from {Path}", _sdkPath);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to initialize Bosch VideoSDK, falling back to ONVIF/ISAPI");
        }

        await Task.CompletedTask;
    }

    private Assembly? LoadAssembly(string name)
    {
        try
        {
            var dllPath = Path.Combine(_sdkPath, $"{name}.dll");
            if (File.Exists(dllPath))
            {
                return Assembly.LoadFrom(dllPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to load {Assembly}", name);
        }
        return null;
    }

    public async Task<Dictionary<string, object>> GetDeviceInfoAsync(string ipAddress, DeviceCredentials credentials)
    {
        var info = new Dictionary<string, object>();
        
        if (!_initialized || _bvipClient == null)
        {
            info["sdkAvailable"] = false;
            return info;
        }

        try
        {
            var connectMethod = _bvipClient.GetType().GetMethod("ConnectAsync") 
                ?? _bvipClient.GetType().GetMethod("Connect");
            
            if (connectMethod != null)
            {
                var task = connectMethod.Invoke(_bvipClient, new object[] { ipAddress, credentials.Username, credentials.Password });
                if (task is Task t) await t;
            }

            var getInfoMethod = _bvipClient.GetType().GetMethod("GetDeviceInfo") 
                ?? _bvipClient.GetType().GetMethod("GetDeviceInfoAsync");
            
            if (getInfoMethod != null)
            {
                var result = getInfoMethod.Invoke(_bvipClient, null);
                if (result is Task task)
                {
                    await task;
                    var resultType = task.GetType().GetProperty("Result")?.GetValue(task);
                    if (resultType != null)
                    {
                        foreach (var prop in resultType.GetType().GetProperties())
                        {
                            info[prop.Name] = prop.GetValue(resultType) ?? "";
                        }
                    }
                }
            }

            info["sdkAvailable"] = true;
            info["connected"] = true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Bosch VideoSDK GetDeviceInfo failed");
            info["sdkAvailable"] = true;
            info["error"] = ex.Message;
        }

        return info;
    }

    public async Task<bool> SetIpConfigAsync(string ipAddress, DeviceCredentials credentials, NetworkConfig config)
    {
        if (!_initialized || _bvipClient == null) return false;

        try
        {
            var method = _bvipClient.GetType().GetMethod("SetNetworkConfig") 
                ?? _bvipClient.GetType().GetMethod("SetIpConfigAsync");
            
            if (method != null)
            {
                var configObj = CreateSdkConfigObject(config);
                var result = method.Invoke(_bvipClient, new[] { configObj });
                if (result is Task t) await t;
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Bosch VideoSDK SetIpConfig failed");
        }
        return false;
    }

    public async Task<bool> UpdateFirmwareAsync(string ipAddress, DeviceCredentials credentials, byte[] firmwareData)
    {
        if (!_initialized || _bvipClient == null) return false;

        try
        {
            var method = _bvipClient.GetType().GetMethod("UpdateFirmwareAsync") 
                ?? _bvipClient.GetType().GetMethod("UpdateFirmware");
            
            if (method != null)
            {
                var result = method.Invoke(_bvipClient, new object[] { firmwareData });
                if (result is Task t) await t;
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Bosch VideoSDK UpdateFirmware failed");
        }
        return false;
    }

    private object? CreateSdkConfigObject(NetworkConfig config)
    {
        if (_bvipAssembly == null) return null;

        try
        {
            var configType = _bvipAssembly.GetType("Bosch.VideoSDK5.BVIP.NetworkConfig")
                ?? _bvipAssembly.GetType("Bosch.VideoSDK5.Core.NetworkConfiguration");
            
            if (configType != null)
            {
                var obj = Activator.CreateInstance(configType);
                if (obj == null) return null;
                SetProperty(obj, "IpAddress", config.IpAddress);
                SetProperty(obj, "SubnetMask", config.SubnetMask);
                SetProperty(obj, "Gateway", config.Gateway);
                SetProperty(obj, "Dns1", config.Dns1);
                SetProperty(obj, "Dns2", config.Dns2);
                SetProperty(obj, "IsDhcp", config.IsDhcp);
                SetProperty(obj, "VlanId", config.VlanId);
                SetProperty(obj, "Hostname", config.Hostname);
                return obj;
            }
        }
        catch { }
        return null;
    }

    private void SetProperty(object obj, string name, object? value)
    {
        if (value == null) return;
        var prop = obj.GetType().GetProperty(name);
        if (prop != null && prop.CanWrite)
        {
            prop.SetValue(obj, value);
        }
    }
}