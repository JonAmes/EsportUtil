using System.Threading.Channels;
using EsportsUtil.Core;
using Microsoft.Extensions.Hosting;

namespace EsportsUtil.Server;

public sealed record Alert(string Type, string Severity, string Message, string? ClientId, string? DevicePin, DateTimeOffset Timestamp);

/// <summary>In-process alert queue (System.Threading.Channels) + liveness watchdog + consumer.
/// The consumer logs today; Phase 3 swaps in the Windows toast notification — same queue.</summary>
public sealed class AlertHub
{
    private readonly Channel<Alert> _ch = Channel.CreateUnbounded<Alert>();
    private readonly List<Alert> _recent = new();
    private readonly object _gate = new();

    public async Task PublishAsync(Alert a)
    {
        lock (_gate) { _recent.Add(a); if (_recent.Count > 50) _recent.RemoveAt(0); }
        await _ch.Writer.WriteAsync(a);
    }

    /// <summary>Drained by the hosted NotificationService (Phase 3) — ingestion never blocks on delivery.</summary>
    public ChannelReader<Alert> Reader => _ch.Reader;

    public List<Alert> Recent() { lock (_gate) return _recent.ToList(); }
}

/// <summary>Heartbeat watchdog: registered devices that miss the timeout window fire a high-priority
/// alert exactly once (re-arm on recovery). Downed machines still heartbeat — downed is never a liveness exception.</summary>
public sealed class LivenessMonitor(DeviceRegistry registry, AlertHub hub, int timeoutSec) : BackgroundService
{
    private readonly HashSet<string> _alerted = new();

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var e in registry.Snapshot())
            {
                var stale = e.LastHeartbeat is null || (now - e.LastHeartbeat.Value).TotalSeconds > timeoutSec;
                if (stale && _alerted.Add(e.ClientId))
                    await hub.PublishAsync(new Alert("heartbeat_lost", "critical",
                        $"no check-in for {timeoutSec}s", e.ClientId, e.DevicePin, now));
                else if (!stale && _alerted.Remove(e.ClientId))
                    await hub.PublishAsync(new Alert("heartbeat_recovered", "info",
                        "check-in resumed", e.ClientId, e.DevicePin, now));
            }
            try { await Task.Delay(TimeSpan.FromSeconds(10), ct); } catch (OperationCanceledException) { break; }
        }
    }
}
