namespace AllNetworkManager.Vendors.Microsens;

using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

/// <summary>UDP transport for the MICROSENS IP protocol (broadcast + unicast).</summary>
public sealed class MicrosensTransport
{
    private readonly ILogger _logger;

    public MicrosensTransport(ILogger logger) => _logger = logger;

    public async Task SendAsync(MicrosensOutgoing outgoing, IPAddress? unicastTarget = null, CancellationToken ct = default)
    {
        var target = unicastTarget != null && !outgoing.Broadcast ? unicastTarget : IPAddress.Broadcast;

        using var udp = new UdpClient();
        udp.EnableBroadcast = true;
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

        var endpoint = new IPEndPoint(target, outgoing.Port);
        await udp.SendAsync(outgoing.Frame.AsMemory(), endpoint, ct).ConfigureAwait(false);

        _logger.LogInformation("MICROSENS frame sent to {Target}:{Port} ({Bytes} bytes)",
            target, outgoing.Port, outgoing.Frame.Length);
    }

    /// <summary>Asks all switches to release their IP and announce again (vendor tool "release" action).</summary>
    public async Task ReleaseAsync(CancellationToken ct = default)
    {
        using var udp = new UdpClient();
        udp.EnableBroadcast = true;
        var payload = MicrosensFrame.BuildDhcpRelease();
        await udp.SendAsync(payload.AsMemory(), new IPEndPoint(IPAddress.Broadcast, MicrosensFrame.SectionedPort), ct)
            .ConfigureAwait(false);
    }
}
