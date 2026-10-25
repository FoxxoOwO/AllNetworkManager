namespace AllNetworkManager.Discovery;

using AllNetworkManager.Core.Discovery;
using AllNetworkManager.Core.Devices;
using AllNetworkManager.Discovery.Protocols;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.NetworkInformation;
using AllNetworkManager.Core.Network;

public class DiscoveryOptions
{
    public string[] Subnets { get; set; } = Array.Empty<string>();
    public bool AutoDetectSubnets { get; set; } = true;
    public int ScanTimeoutSeconds { get; set; } = 10;
    public bool EnableWsDiscovery { get; set; } = true;
    public bool EnableSsdp { get; set; } = true;
    public bool EnableMndp { get; set; } = true;
    public bool EnablePortScan { get; set; } = false;
    public int[] CommonPorts { get; set; } = { 80, 443, 8080, 8728, 8729, 8291, 5678, 3702, 1900 };

    /// <summary>Ports swept per address when the user asks for an explicit range or subnet.</summary>
    public int[] ActiveScanPorts { get; set; } = { 80, 443, 554, 8000, 8080, 8728, 8291 };

    /// <summary>Parallel address probes during an active scan.</summary>
    public int ActiveScanConcurrency { get; set; } = 96;

    /// <summary>Per-address probe budget during an active scan.</summary>
    public int ActiveScanTimeoutMs { get; set; } = 700;
}

public class DiscoveryService : IDiscoveryService
{
    private readonly ILogger<DiscoveryService> _logger;
    private readonly DiscoveryOptions _options;
    private readonly IEnumerable<IDiscoveryProtocol> _protocols;

    public string Name => "Unified Discovery Service";
    public string[] SupportedProtocols => _protocols.Select(p => p.ProtocolName).ToArray();

    public event EventHandler<DiscoveredDevice>? DeviceDiscovered;
    public event EventHandler<DiscoveryProgress>? ProgressChanged;

    public DiscoveryService(
        ILogger<DiscoveryService> logger,
        IOptions<DiscoveryOptions> options,
        IEnumerable<IDiscoveryProtocol> protocols)
    {
        _logger = logger;
        _options = options.Value;
        _protocols = protocols;
    }

    public async Task<IEnumerable<DiscoveredDevice>> DiscoverAsync(CancellationToken ct = default)
    {
        var subnets = await GetSubnetsToScanAsync(ct).ConfigureAwait(false);
        return await DiscoverAsync(subnets, activeScan: false, ct).ConfigureAwait(false);
    }

    public async Task<IEnumerable<DiscoveredDevice>> DiscoverAsync(
        IReadOnlyList<string> targets, bool activeScan, CancellationToken ct = default)
    {
        var allDevices = new List<DiscoveredDevice>();

        // Dedupe inside a single run only. Keeping this across runs used to make every scan after the
        // first report zero devices (the dictionary was never cleared).
        var seen = new Dictionary<string, DiscoveredDevice>(StringComparer.OrdinalIgnoreCase);
        var count = Math.Max(targets.Count, 1);

        for (int i = 0; i < targets.Count; i++)
        {
            var target = targets[i];
            ReportProgress(target, allDevices.Count, i, count, "Skenuji " + target);

            var found = (await DiscoverAsync(target, ct).ConfigureAwait(false)).ToList();

            if (activeScan)
            {
                if (AddressRange.TryParse(target, out var addresses, out var error))
                {
                    ReportProgress(target, allDevices.Count, i, count, $"Procházím {addresses.Count} adres");
                    found.AddRange(await SweepAsync(addresses, ct).ConfigureAwait(false));
                }
                else
                {
                    _logger.LogWarning("Nelze aktivně prohledat {Target}: {Error}", target, error);
                }
            }

            foreach (var d in found)
            {
                if (seen.TryGetValue(d.IpAddress, out var known))
                {
                    // keep whichever record carries more detail (e.g. SSDP vs WS-Discovery)
                    if ((d.Properties?.Count ?? 0) > (known.Properties?.Count ?? 0))
                        seen[d.IpAddress] = d;
                    continue;
                }

                seen[d.IpAddress] = d;
                allDevices.Add(d);
                DeviceDiscovered?.Invoke(this, d);
            }

            ReportProgress(target, allDevices.Count, i + 1, count, "Hotovo " + target);
        }

        ReportProgress("", allDevices.Count, targets.Count, targets.Count, "Complete");
        return allDevices;
    }

    /// <summary>
    /// Probes every address on the active-scan ports. This is what finds devices that stay silent on
    /// multicast (fixed IP, no SSDP/WS-Discovery, another VLAN), at the cost of a TCP sweep.
    /// </summary>
    private async Task<IReadOnlyList<DiscoveredDevice>> SweepAsync(
        IReadOnlyList<string> addresses, CancellationToken ct)
    {
        if (addresses.Count == 0) return Array.Empty<DiscoveredDevice>();

        var results = new System.Collections.Concurrent.ConcurrentBag<DiscoveredDevice>();
        var gate = new SemaphoreSlim(Math.Max(1, _options.ActiveScanConcurrency));
        var ports = _options.ActiveScanPorts;
        var timeoutMs = Math.Max(100, _options.ActiveScanTimeoutMs);

        var probes = addresses.Select(async ip =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));

                foreach (var port in ports)
                {
                    foreach (var protocol in _protocols.Where(p => p.SupportsPort(port)))
                    {
                        try
                        {
                            var device = await protocol.ProbeAsync(ip, port, timeout.Token).ConfigureAwait(false);
                            if (device != null)
                            {
                                results.Add(device);
                                return;
                            }
                        }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                        {
                            // this address did not answer in time, move on
                            return;
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Probe {Ip}:{Port} via {Protocol} failed", ip, port, protocol.ProtocolName);
                        }
                    }
                }
            }
            finally
            {
                gate.Release();
            }
        }).ToList();

        try
        {
            await Task.WhenAll(probes).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // the caller cancelled the scan; whatever was already found is still useful
        }

        return results.ToList();
    }

    public async Task<IEnumerable<DiscoveredDevice>> DiscoverAsync(string subnet, CancellationToken ct = default)
    {
        ReportProgress(subnet, 0, 0, 1, "Probing with " + string.Join(", ", _protocols.Select(p => p.ProtocolName)));

        var scans = _protocols.Select(protocol => ScanSafeAsync(protocol, subnet, ct)).ToList();
        var results = await Task.WhenAll(scans);

        var allDevices = results.SelectMany(r => r).ToList();

        if (_options.EnablePortScan)
        {
            var ips = GetIpsInSubnet(subnet);
            foreach (var ip in ips)
            {
                ct.ThrowIfCancellationRequested();
                foreach (var port in _options.CommonPorts)
                {
                    foreach (var protocol in _protocols.Where(p => p.SupportsPort(port)))
                    {
                        try
                        {
                            var device = await protocol.ProbeAsync(ip, port, ct);
                            if (device != null)
                                allDevices.Add(device);
                        }
                        catch { }
                    }
                }
            }
        }

        return allDevices.DistinctBy(d => d.IpAddress).ToList();
    }

    public async Task<DiscoveredDevice?> DiscoverSingleAsync(string ipAddress, CancellationToken ct = default)
    {
        foreach (var protocol in _protocols)
        {
            foreach (var port in _options.CommonPorts.Where(protocol.SupportsPort))
            {
                try
                {
                    var device = await protocol.ProbeAsync(ipAddress, port, ct);
                    if (device != null)
                        return device;
                }
                catch { }
            }
        }
        return null;
    }

    private async Task<IReadOnlyList<DiscoveredDevice>> ScanSafeAsync(
        IDiscoveryProtocol protocol, string subnet, CancellationToken ct)
    {
        try
        {
            var devices = await protocol.ScanAsync(subnet, ct);
            return devices.ToList();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Protocol {Protocol} failed on subnet {Subnet}", protocol.ProtocolName, subnet);
            return Array.Empty<DiscoveredDevice>();
        }
    }

    private async Task<string[]> GetSubnetsToScanAsync(CancellationToken ct)
    {
        if (_options.Subnets.Length > 0)
            return _options.Subnets;

        if (!_options.AutoDetectSubnets)
            return Array.Empty<string>();

        var subnets = new List<string>();
        var netInterfaces = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces();

        foreach (var ni in netInterfaces.Where(n => n.OperationalStatus == OperationalStatus.Up 
            && n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
        {
            var props = ni.GetIPProperties();
            foreach (var addr in props.UnicastAddresses.Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork))
            {
                var ip = addr.Address;
                var mask = addr.IPv4Mask;
                if (mask != null)
                {
                    var network = GetNetworkAddress(ip, mask);
                    var cidr = GetCidr(mask);
                    subnets.Add($"{network}/{cidr}");
                }
            }
        }

        return subnets.Distinct().ToArray();
    }

    private IPAddress GetNetworkAddress(IPAddress ip, IPAddress mask)
    {
        var ipBytes = ip.GetAddressBytes();
        var maskBytes = mask.GetAddressBytes();
        var netBytes = new byte[4];
        for (int i = 0; i < 4; i++)
            netBytes[i] = (byte)(ipBytes[i] & maskBytes[i]);
        return new IPAddress(netBytes);
    }

    private int GetCidr(IPAddress mask)
    {
        var bytes = mask.GetAddressBytes();
        int cidr = 0;
        foreach (var b in bytes)
        {
            for (int i = 7; i >= 0; i--)
            {
                if ((b & (1 << i)) != 0) cidr++;
                else return cidr;
            }
        }
        return cidr;
    }

    private IEnumerable<string> GetIpsInSubnet(string cidr)
    {
        var parts = cidr.Split('/');
        var ip = IPAddress.Parse(parts[0]);
        var prefixLen = int.Parse(parts[1]);
        var ipBytes = ip.GetAddressBytes();
        
        if (prefixLen is < 8 or > 30) yield break;

        var hostBits = 32 - prefixLen;
        var count = Math.Min(1 << hostBits, 256); // Limit to /24 max
        
        for (int i = 1; i < count - 1; i++) // Skip network and broadcast
        {
            var addrBytes = (byte[])ipBytes.Clone();
            var val = i;
            for (int j = 3; j >= 0; j--)
            {
                addrBytes[j] = (byte)(addrBytes[j] | (val & 0xFF));
                val >>= 8;
            }
            yield return new IPAddress(addrBytes).ToString();
        }
    }

    private void ReportProgress(string subnet, int found, int scanned, int total, string operation)
    {
        ProgressChanged?.Invoke(this, new DiscoveryProgress(
            CurrentSubnet: subnet,
            DevicesFound: found,
            SubnetsScanned: scanned,
            TotalSubnets: total,
            CurrentOperation: operation
        ));
    }
}