using System.Text.Json.Nodes;

namespace EsportsUtil.Core;

/// <summary>SteamCMD presence verdict. Embedded verbatim into machine_state.steamCmd by the
/// State Module. source: config | winget_link | winget_package | path (how we found it);
/// install_state: null (found / never needed) | installing | installed | failed | unavailable.</summary>
public sealed record SteamCmdState(bool Found, string? Path, string? Source, string? InstallState, string? Error)
{
    public JsonObject ToNode() => new()
    {
        ["found"] = Found,
        ["path"] = Path,
        ["source"] = Source,
        ["install_state"] = InstallState,
        ["error"] = Error,
    };
}

/// <summary>SteamCMD bootstrap seam. Each agent beat locates steamcmd via a pure precedence chain
/// (explicit absolute config path → winget link → winget portable package → PATH). When missing and
/// due, ONE winget bootstrap runs fire-and-forget (Valve.SteamCMD, identity verified against
/// winget-pkgs; the operator asked for winget specifically — a plain exe, no PowerShell), retried at
/// most once per RetryFailMin. Download/update jobs then run through JobRunner with anonymous login.
/// Dev/test seams: ESU_FAKE_WINGET=ok|fail (fake bootstrap outcome). Without a seam a non-Windows
/// box answers install_state "unavailable" instead of throwing. Real winget install + real
/// anonymous app_update stay Windows-box verification gates.</summary>
public static class SteamCmdWatch
{
    public const string FakeEnvVar = "ESU_FAKE_WINGET";
    public const string WingetId = "Valve.SteamCMD";
    public const int RetryFailMin = 30;

    private static SteamCmdState? _last;
    private static volatile bool _installing;
    private static long _lastAttemptUnix;   // 0 = never attempted
    public const int ProbeTtlSec = 300;     // locate verdict cache lifetime (PATH probe costs a process spawn)
    private static bool _haveProbe;
    private static long _probeUnix;
    private static string? _cachePath, _cacheSource;

    public static SteamCmdState? Last => _last;
    public static bool IsInstalling => _installing;

    /// <summary>Test isolation only (shared static across the dep-free runner).</summary>
    public static void Reset()
    { _last = null; _installing = false; _lastAttemptUnix = 0; _haveProbe = false; _cachePath = _cacheSource = null; }

    /// <summary>Pure precedence chain. configured is the raw config value — honored only when it is
    /// an absolute path that exists; wingetLink/wingetPackage are candidate file paths (null when
    /// the location does not apply); onPath is the PATH probe verdict.</summary>
    public static (string? Path, string? Source) Find(
        string? configured, Func<string, bool> fileExists,
        string? wingetLink, string? wingetPackage, bool onPath)
    {
        if (configured is not null && IsRooted(configured) && fileExists(configured))
            return (configured, "config");
        if (wingetLink is not null && fileExists(wingetLink))
            return (wingetLink, "winget_link");
        if (wingetPackage is not null && fileExists(wingetPackage))
            return (wingetPackage, "winget_package");
        if (onPath) return ("steamcmd", "path");
        return (null, null);
    }

    /// <summary>Absolute on the current OS OR a Windows drive path (config values authored on
    /// Windows stay meaningful when parsed on the Linux dev box — cross-target parity).</summary>
    public static bool IsRooted(string p) =>
        Path.IsPathRooted(p) ||
        (p.Length > 2 && p[1] == ':' && (p[2] == '\\' || p[2] == '/'));

    /// <summary>Pure portable-package pick: directories named Valve.SteamCMD_* that contain
    /// steamcmd.exe; deterministic (sorted) so tests are stable.</summary>
    public static string? PickPortablePackage(string packagesDir,
        Func<string, string[]> dirListing, Func<string, bool> fileExists)
    {
        foreach (var d in dirListing(packagesDir)
                     .Where(d => d.StartsWith("Valve.SteamCMD_", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var exe = Path.Combine(packagesDir, d, "steamcmd.exe");
            if (fileExists(exe)) return exe;
        }
        return null;
    }

    /// <summary>Pure bootstrap policy: attempt only when missing, nothing in flight, auto-install
    /// enabled, and never more than once per RetryFailMin (the first attempt is immediate).</summary>
    public static bool EnsureDue(long nowUnix, long? lastAttemptUnix, bool found, bool installing, bool autoInstall)
        => !found && !installing && autoInstall &&
           (lastAttemptUnix is null || nowUnix - lastAttemptUnix.Value >= (long)RetryFailMin * 60);

    /// <summary>Pure bootstrap-outcome decision: the fake seam wins; winget exists only on Windows
    /// ("unavailable" elsewhere — never an exception on the heartbeat spine); "run" means invoke it.</summary>
    public static string InstallOutcome(string? fakeEnv, bool isWindows)
        => fakeEnv switch
        {
            null => isWindows ? "run" : "unavailable",
            "ok" => "installed",
            "fail" => "failed",
            _ => "failed",   // unknown seam value: fail loudly rather than silently running winget
        };

    /// <summary>Winget argv for the bootstrap (identity-pinned, fully unattended).</summary>
    public static List<string> WingetArgs() => new()
    {
        "install", "-e", "--id", WingetId,
        "--accept-package-agreements", "--accept-source-agreements",
        "--silent", "--disable-interactivity",
    };

    /// <summary>Beat entry point: locate steamcmd; when missing and due, kick the winget bootstrap
    /// fire-and-forget (a slow download must never stall the heartbeat spine). Sets Last every call
    /// so the report hash carries the current verdict; flips let the agent force a prompt report.</summary>
    public static Task<SteamCmdState> CheckAsync(EsportsConfig cfg, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        // The PATH probe spawns a process — cache the locate verdict so a 1s heartbeat does not
        // burn working set re-probing a missing steamcmd every beat (re-probe every ProbeTtlSec).
        if (_haveProbe && now - _probeUnix > ProbeTtlSec) _haveProbe = false;
        if (!_haveProbe) { (_cachePath, _cacheSource) = Locate(cfg); _probeUnix = now; _haveProbe = true; }
        var (path, source) = (_cachePath, _cacheSource);
        string? installState = null, error = null;

        if (path is null)
        {
            if (_installing) installState = "installing";
            else if (EnsureDue(now, _lastAttemptUnix == 0 ? null : _lastAttemptUnix, false, false, cfg.SteamCmdAutoInstall))
            {
                _lastAttemptUnix = now;
                // Task.Run: a fake/immediate outcome must NOT finish inline — CheckAsync's final
                // _last assignment below would clobber the install verdict before anyone sees it.
                _ = Task.Run(async () =>
                {
                    await InstallAsync(cfg, ct).ConfigureAwait(false);
                    var s = _last!;
                    Console.WriteLine(s.Found
                        ? $"steamcmd bootstrapped via winget: {s.Path}"
                        : $"steamcmd bootstrap did not land: {s.InstallState} {s.Error}");
                });
                installState = "installing";
            }
            else installState = _last?.InstallState;   // carry the previous verdict (failed/unavailable) until resolved
            if (installState == "failed") error = _last?.Error;
        }

        _last = new SteamCmdState(path is not null, path, source, installState, error);
        return Task.FromResult(_last);
    }

    private static (string? Path, string? Source) Locate(EsportsConfig cfg)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string? link = null, packages = null;
        if (!string.IsNullOrEmpty(local))
        {
            var l = Path.Combine(local, "Microsoft", "WinGet", "Links", "steamcmd.exe");
            link = File.Exists(l) ? l : null;
            var p = Path.Combine(local, "Microsoft", "WinGet", "Packages");
            packages = Directory.Exists(p) ? p : null;
        }
        var pkg = packages is null ? null : PickPortablePackage(packages, Directory.GetDirectories, File.Exists);
        var exe = OperatingSystem.IsWindows() ? "steamcmd.exe" : "steamcmd";
        var onPath = OnPath(OperatingSystem.IsWindows() ? "where.exe" : "which", exe);
        return Find(cfg.SteamCmdPath, File.Exists, link, pkg, onPath);
    }

    private static bool OnPath(string probe, string exe)
    {
        try
        {
            using var p = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo(probe, exe)
                { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true },
            };
            p.Start();
            p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
            if (!p.WaitForExit(10_000)) { try { p.Kill(true); } catch { } return false; }
            return p.ExitCode == 0;
        }
        catch (Exception) { return false; }   // no probe binary on the box: treat as not-on-PATH
    }

    private static async Task InstallAsync(EsportsConfig cfg, CancellationToken ct)
    {
        _installing = true;
        string? error = null;
        var outcome = InstallOutcome(Environment.GetEnvironmentVariable(FakeEnvVar), OperatingSystem.IsWindows());
        try
        {
            if (outcome == "run")
            {
                var (exit, stderr) = await RunWingetAsync(WingetArgs(), ct).ConfigureAwait(false);
                if (exit != 0) { outcome = "failed"; error = $"winget exit {exit}: {Tail(stderr)}"; }
            }
            if (outcome is "installed" or "run")
            {
                // winget says ok — trust but verify: only report installed if it is actually locatable.
                var (path, source) = Locate(cfg);
                _last = new SteamCmdState(path is not null, path, source, path is not null ? "installed" : "failed",
                    path is null ? "winget reported success but steamcmd is still not locatable" : null);
            }
            else if (outcome == "unavailable")
                _last = new SteamCmdState(false, null, null, "unavailable", null);
            else
                _last = new SteamCmdState(false, null, null, "failed", error);
        }
        finally { _installing = false; }
    }

    private static string Tail(string? s) => string.IsNullOrWhiteSpace(s) ? "" :
        s.Length <= 300 ? s.Trim() : s[^300..].Trim();

    private static async Task<(int Exit, string Stderr)> RunWingetAsync(List<string> args, CancellationToken ct)
    {
        using var p = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo("winget", string.Join(' ', args))
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true },
        };
        p.Start();
        var outT = p.StandardOutput.ReadToEndAsync();
        var errT = p.StandardError.ReadToEndAsync();
        using var reg = ct.Register(() => { try { p.Kill(true); } catch { } });
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        _ = await outT;
        return (p.ExitCode, await errT);
    }
}
