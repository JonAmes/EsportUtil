using System.Net;
using System.Net.Sockets;
using EsportsUtil.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EsportsUtil.Server;

/// <summary>T35: answers the LAN autodetection probe on UDP discovery_port. A bind collision
/// (two servers on one box, or a squatter) is a warn, never fatal — the HTTP side is unaffected.
/// Each reply advertises the URL of the interface the probe arrived on, so LAN agents get a
/// directly reachable address without ever typing one.</summary>
public sealed class DiscoveryResponder : IHostedService
{
    private readonly EsportsConfig _cfg;
    private readonly ILogger _log;
    private UdpClient? _udp;
    private Task? _loop;
    private readonly CancellationTokenSource _cts = new();

    public DiscoveryResponder(EsportsConfig cfg, ILogger log) { _cfg = cfg; _log = log; }

    public Task StartAsync(CancellationToken ct)
    {
        if (_cfg.DiscoveryPort == 0) return Task.CompletedTask;
        try
        {
            _udp = new UdpClient(AddressFamily.InterNetwork);
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, _cfg.DiscoveryPort));
        }
        catch (SocketException e)
        {
            _log.LogWarning("discovery probe port {Port} unavailable ({Msg}) — LAN autodetection disabled, dashboard unaffected",
                _cfg.DiscoveryPort, e.Message);
            _udp?.Dispose();
            _udp = null;
            return Task.CompletedTask;
        }
        _loop = Task.Run(RunAsync);
        _log.LogInformation("LAN autodetection answering probes on udp/{Port}", _cfg.DiscoveryPort);
        return Task.CompletedTask;
    }

    private async Task RunAsync()
    {
        var udp = _udp!;
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var got = await udp.ReceiveAsync(_cts.Token);
                if (System.Text.Encoding.UTF8.GetString(got.Buffer).Trim() != "ESU-DISCOVER") continue;
                // the connectionless socket reports the LOCAL address the datagram landed on
                var iface = (udp.Client.LocalEndPoint as IPEndPoint)?.Address ?? IPAddress.Loopback;
                var url = Discovery.ResolveAdvertiseUrl(_cfg.ServerUrl, iface, _cfg.Port);
                await udp.SendAsync(System.Text.Encoding.UTF8.GetBytes(Discovery.ReplyJson(url, SelfUpdateWatch.CurrentVersion)), got.RemoteEndPoint);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException) { }   // transient receive error — responder stays down only if the socket died
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _cts.Cancel();
        _udp?.Dispose();
        if (_loop is not null) try { await _loop; } catch { }
    }
}
