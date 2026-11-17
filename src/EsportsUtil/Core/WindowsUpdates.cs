using System.Runtime.Versioning;
using System.Text.Json.Nodes;

namespace EsportsUtil.Core;

/// <summary>One Windows Update poll outcome. Embedded verbatim into machine_state.windowsUpdate
/// by the State Module (single writer) — the report hash riding last_scan_unix is what flows a
/// fresh summary to the server, registry, and the dashboard state pill.</summary>
public sealed record WindowsUpdateState(string Status, int? PendingCount, long? LastScanUnix, string? Error)
{
    public JsonObject ToNode() => new()
    {
        ["status"] = Status,               // ok | updates | error | unsupported
        ["pending_count"] = PendingCount,  // null when the poll could not answer
        ["last_scan_unix"] = LastScanUnix,
        ["error"] = Error,
    };
}

/// <summary>Windows Update polling seam. The agent polls WUA (COM Microsoft.Update.Session)
/// on a timed interval AND on operator request (scan_updates directive via heartbeat ack).
/// Due() is pure; the scan itself runs fire-and-forget on its own task so a slow WUA search
/// never stalls the heartbeat spine. Dev/test hook: ESU_FAKE_UPDATES=<n> forces a fake result
/// on any OS (Linux suite, browser QA); without it a non-Windows box answers status "unsupported"
/// rather than throwing. Real WUA behavior stays a Windows-box verification gate.</summary>
public static class UpdateWatch
{
    public const string FakeEnvVar = "ESU_FAKE_UPDATES";

    private static WindowsUpdateState? _last;
    private static volatile bool _scanning;
    private static volatile bool _requested;

    public static WindowsUpdateState? Last => _last;
    public static bool IsScanning => _scanning;
    public static bool HasRequestedScan => _requested;
    public static void RequestScan() => _requested = true;

    /// <summary>Test isolation only (shared static across the dep-free runner).</summary>
    public static void Reset() { _last = null; _scanning = false; _requested = false; }

    /// <summary>Pure polling policy: requested scans always run (unless one is in flight); the timer
    /// fires when never-scanned or the interval lapsed; intervalMin &lt;= 0 disables the timer entirely.</summary>
    public static bool Due(long nowUnix, long? lastScanUnix, int intervalMin, bool scanning, bool requested)
    {
        if (scanning) return false;
        if (requested) return true;
        if (intervalMin <= 0) return false;
        return lastScanUnix is null || nowUnix - lastScanUnix.Value >= (long)intervalMin * 60;
    }

    public static async Task ScanAsync(CancellationToken ct = default)
    {
        _scanning = true;
        _requested = false;
        try
        {
            WindowsUpdateState result;
            var fake = Environment.GetEnvironmentVariable(FakeEnvVar);
            if (int.TryParse(fake, out var n) && n >= 0)
            {
                try { await Task.Delay(300, ct); } catch (OperationCanceledException) { return; }
                result = new(n > 0 ? "updates" : "ok", n, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), null);
            }
            else if (OperatingSystem.IsWindows())
                result = ProbeWindowsUpdateAgent();
            else
                result = new("unsupported", null, null, "Windows Update API is only available on Windows");
            _last = result;
        }
        catch (Exception e) { _last = new("error", null, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), e.Message); }
        finally { _scanning = false; }
    }

    [SupportedOSPlatform("windows")]
    private static WindowsUpdateState ProbeWindowsUpdateAgent()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        try
        {
            var sessionType = Type.GetTypeFromProgID("Microsoft.Update.Session")
                ?? throw new InvalidOperationException("Microsoft.Update.Session not registered");
            dynamic session = Activator.CreateInstance(sessionType)!;
            dynamic searcher = session.CreateUpdateSearcher();
            searcher.ClientApplicationID = "EsportsUtil";
            dynamic search = searcher.Search(0);          // 0 = every category, not just recent history
            int pending = 0;
            foreach (dynamic u in search.Updates)
                if (!(bool)u.IsInstalled) pending++;
            return new(pending > 0 ? "updates" : "ok", pending, now, null);
        }
        catch (Exception e) { return new("error", null, now, e.Message); }   // WUA busy/service stopped: honest error, never a crash
    }
}
