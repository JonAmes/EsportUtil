using System.Runtime.Versioning;
using System.Text.Json.Nodes;

namespace EsportsUtil.Core;

/// <summary>One Windows Update poll outcome, optionally carrying an in-flight/terminal
/// install run (T33). Embedded verbatim into machine_state.windowsUpdate by the State Module
/// (single writer) — the report hash riding last_scan_unix (and a changing install_done count)
/// is what flows a fresh summary to the server, registry, and the dashboard pill/chip.</summary>
public sealed record WindowsUpdateState(
    string Status, int? PendingCount, long? LastScanUnix, string? Error,
    string? InstallState = null,     // running | done | failed (absent = no install run)
    int? InstallDone = null, int? InstallTotal = null, string? InstallError = null,
    bool? RebootPending = null)      // true = WUA demands a reboot and the agent armed one
{
    public JsonObject ToNode() => new()
    {
        ["status"] = Status,               // ok | updates | error | unsupported
        ["pending_count"] = PendingCount,  // null when the poll could not answer
        ["last_scan_unix"] = LastScanUnix,
        ["error"] = Error,
        ["install_state"] = InstallState,
        ["install_done"] = InstallDone,
        ["install_total"] = InstallTotal,
        ["install_error"] = InstallError,
        ["reboot_pending"] = RebootPending,
    };
}

/// <summary>Windows Update polling + install seam. The agent polls WUA (COM Microsoft.Update.Session)
/// on a timed interval AND on operator request (scan_updates directive via heartbeat ack). Due() is
/// pure; the scan/install runs fire-and-forget on their own task so a slow WUA search never stalls
/// the heartbeat spine. T33: install_updates directive → InstallAsync installs EVERY pending update
/// one at a time (per-update progress streams through onStage → report hash → dashboard chip), then
/// a reboot is flagged ONLY if WUA says one is required. Dev/test hooks (cross-OS): ESU_FAKE_UPDATES
/// =<n> forces a fake scan result, ESU_FAKE_INSTALL=<ms per update> simulates the staged install of
/// those n updates, ESU_FAKE_REBOOT=1 forces the reboot-required verdict. Without the hooks a
/// non-Windows box answers "unsupported" rather than throwing; real WUA install stays a Windows gate.</summary>
public static class UpdateWatch
{
    public const string FakeEnvVar = "ESU_FAKE_UPDATES";
    public const string FakeInstallEnvVar = "ESU_FAKE_INSTALL";
    public const string FakeRebootEnvVar = "ESU_FAKE_REBOOT";

    private static WindowsUpdateState? _last;
    private static volatile bool _scanning;
    private static volatile bool _requested;
    private static volatile bool _installing;
    private static volatile bool _installRequested;

    public static WindowsUpdateState? Last => _last;
    public static bool IsScanning => _scanning;
    public static bool HasRequestedScan => _requested;
    public static bool IsInstalling => _installing;
    public static bool HasRequestedInstall => _installRequested;
    public static void RequestScan() => _requested = true;
    public static void RequestInstall() => _installRequested = true;

    /// <summary>Test isolation only (shared static across the dep-free runner).</summary>
    public static void Reset()
    {
        _last = null; _scanning = false; _requested = false;
        _installing = false; _installRequested = false;
    }

    /// <summary>Pure polling policy: requested scans always run (unless one is in flight); the timer
    /// fires when never-scanned or the interval lapsed; intervalMin &lt;= 0 disables the timer entirely.</summary>
    public static bool Due(long nowUnix, long? lastScanUnix, int intervalMin, bool scanning, bool requested)
    {
        if (scanning) return false;
        if (requested) return true;
        if (intervalMin <= 0) return false;
        return lastScanUnix is null || nowUnix - lastScanUnix.Value >= (long)intervalMin * 60;
    }

    /// <summary>Pure install policy (T33): installs are operator-directive driven only — there is no
    /// timer — and never run while a scan or another install is in flight.</summary>
    public static bool DueInstall(bool scanning, bool installing, bool installRequested) =>
        installRequested && !installing && !scanning;

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
            _last = MergeInstall(result, _last);
        }
        catch (Exception e) { _last = new("error", null, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), e.Message); }
        finally { _scanning = false; }
    }

    /// <summary>Install EVERY pending update on this machine (T33). onStage fires after every progress
    /// change so the agent loop can force a prompt report AND refresh the overlay progress file.
    /// Never throws: WUA busy, EULA refusals and per-update failures land in install_error.</summary>
    public static async Task InstallAsync(Action? onStage = null, CancellationToken ct = default)
    {
        _installing = true;
        _installRequested = false;
        var startedScan = _last?.LastScanUnix ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        try
        {
            var fakeMs = Environment.GetEnvironmentVariable(FakeInstallEnvVar);
            if (int.TryParse(fakeMs, out var stepMs) && stepMs >= 0)
            {
                // Cross-OS seam: stage the ESU_FAKE_UPDATES count one update at a time, exactly the
                // progress shape the real WUA path publishes (running done/total → done terminal).
                var total = int.TryParse(Environment.GetEnvironmentVariable(FakeEnvVar), out var n) ? Math.Max(n, 0) : 0;
                PublishRunning(0, total, startedScan); onStage?.Invoke();
                for (int i = 1; i <= total; i++)
                {
                    try { await Task.Delay(stepMs, ct); }
                    catch (OperationCanceledException) { return; }   // loop dying: state stays running, server heals it as aborted
                    PublishRunning(i, total, startedScan); onStage?.Invoke();
                }
                var fakeReboot = Environment.GetEnvironmentVariable(FakeRebootEnvVar) == "1";
                _last = new("ok", 0, startedScan, null,
                    "done", total, total, null, fakeReboot ? true : null);
            }
            else if (OperatingSystem.IsWindows())
                InstallWindowsUpdates(onStage, startedScan);
            else
            {
                _last = new("unsupported", null, null, "Windows Update API is only available on Windows",
                    "failed", 0, 0, "not on Windows", null);
            }
        }
        catch (Exception e)
        {
            _last = new("error", null, startedScan, e.Message, "failed", null, null, e.Message, null);
        }
        finally { _installing = false; }
    }

    private static void PublishRunning(int done, int total, long scanUnix)
        => _last = (_last ?? new("updates", total, scanUnix, null)) with
        {
            Status = "updates",
            LastScanUnix = scanUnix,
            InstallState = "running",
            InstallDone = done,
            InstallTotal = total,
            InstallError = null,
            RebootPending = null,
        };

    /// <summary>A scan keeps any in-flight/last install run visible — a poll that lands mid-install
    /// must not erase the progress the dashboard is rendering.</summary>
    private static WindowsUpdateState MergeInstall(WindowsUpdateState fresh, WindowsUpdateState? prev) =>
        prev is { InstallState: { Length: > 0 } s } && (s == "running" || fresh.LastScanUnix == prev.LastScanUnix)
            ? fresh with { InstallState = prev.InstallState, InstallDone = prev.InstallDone, InstallTotal = prev.InstallTotal, InstallError = prev.InstallError, RebootPending = prev.RebootPending }
            : fresh;

    [SupportedOSPlatform("windows")]
    private static WindowsUpdateState ProbeWindowsUpdateAgent()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        try
        {
            dynamic session = CreateSession();
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

    /// <summary>Real WUA install (Windows gate): search → per-update AcceptEula + one-update installer
    /// so each completion is a progress beat; WUA ResultCode 2=Succeeded 3=SucceededWithErrors.
    /// Reboot verdict comes from Microsoft.Update.SystemInfo.RebootRequired after the run.</summary>
    [SupportedOSPlatform("windows")]
    private static void InstallWindowsUpdates(Action? onStage, long scanUnix)
    {
        dynamic session = CreateSession();
        dynamic searcher = session.CreateUpdateSearcher();
        searcher.ClientApplicationID = "EsportsUtil";
        dynamic search = searcher.Search(0);
        var pending = new List<dynamic>();
        foreach (dynamic u in search.Updates)
            if (!(bool)u.IsInstalled) pending.Add(u);

        int total = pending.Count, done = 0, ok = 0;
        var failures = new List<string>();
        PublishRunning(0, total, scanUnix); onStage?.Invoke();
        foreach (dynamic u in pending)
        {
            try { u.AcceptEula(); } catch (Exception e) { failures.Add($"EULA: {e.Message}"); }
            try
            {
                dynamic coll = session.CreateUpdateCollection();
                coll.Add(u);
                dynamic installer = session.CreateUpdateInstaller();
                installer.ClientApplicationID = "EsportsUtil";
                installer.Updates = coll;
                dynamic res = installer.Install();
                int code = (int)res.ResultCode;
                if (code is 2 or 3) ok++; else failures.Add($"update {done + 1} result {code}");
            }
            catch (Exception e) { failures.Add($"update {done + 1}: {e.Message}"); }
            done++;
            PublishRunning(done, total, scanUnix); onStage?.Invoke();
        }

        bool reboot = false;
        try
        {
            var t = Type.GetTypeFromProgID("Microsoft.Update.SystemInfo")
                ?? throw new InvalidOperationException("Microsoft.Update.SystemInfo not registered");
            dynamic si = Activator.CreateInstance(t)!;
            reboot = (bool)si.RebootRequired;
        }
        catch { /* verdict stays honest-false: never reboot without WUA asking */ }

        int remaining = total - ok;
        _last = new(remaining > 0 ? "updates" : "ok", remaining, scanUnix, null,
            failures.Count == 0 ? "done" : "failed", done, total,
            failures.Count == 0 ? null : string.Join("; ", failures),
            failures.Count == 0 && reboot ? true : null);
    }

    [SupportedOSPlatform("windows")]
    private static dynamic CreateSession()
    {
        var sessionType = Type.GetTypeFromProgID("Microsoft.Update.Session")
            ?? throw new InvalidOperationException("Microsoft.Update.Session not registered");
        return Activator.CreateInstance(sessionType)!;
    }
}
