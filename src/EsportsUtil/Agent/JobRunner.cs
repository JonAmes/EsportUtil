using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using EsportsUtil.Core;

namespace EsportsUtil.Agent;

/// <summary>Phase 4 job execution. Handlers are allow-listed by kind; every parameter is
/// re-validated locally before anything runs (argv is always BUILT by the agent from structured
/// fields — the wire never carries command strings). Destructive handlers re-derive the machine's
/// current state by rescanning, so a stale server-side resolution can never delete the wrong copy.</summary>
public sealed class JobRunner(HttpClient http, EsportsConfig cfg)
{
    public string ContentCacheDir => Path.Combine(cfg.DataDir, "content");

    // ---------- pure, unit-tested decision functions ----------

    /// <summary>SteamCMD argv for one game: force_install_dir FIRST (before the app update), anonymous
    /// login, validate. Throws on anything that isn't a bare numeric app id — injection defense.</summary>
    public static List<string> SteamCmdArgs(string installDir, string appId)
    {
        if (appId is null || !appId.All(char.IsAsciiDigit) || appId.Length is 0 or > 12)
            throw new ArgumentException($"app id must be digits only, got '{appId}'");
        if (string.IsNullOrWhiteSpace(installDir))
            throw new ArgumentException("install dir required for steamcmd update");
        return new List<string>
        {
            "+force_install_dir", installDir,
            "+login", "anonymous",
            "+app_update", appId, "validate",
            "+quit",
        };
    }

    /// <summary>Which copies to delete for a duplicate resolution: every reported path for the game
    /// EXCEPT the server-resolved keep-path. Throws if the keep-path isn't among the current copies
    /// (machine changed since the server resolved → refuse rather than guess).</summary>
    public static List<string> DuplicateCleanupPlan(IEnumerable<string> currentPaths, string keepPath)
    {
        var paths = currentPaths.ToList();
        if (!paths.Contains(keepPath, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"keep-path '{keepPath}' is not among this machine's copies — refusing");
        return paths.Where(p => !p.Equals(keepPath, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>Uninstall precedence from the reported inventory entry: registry QuietUninstallString,
    /// then winget-by-name, then interactive UninstallString. Null = nothing runnable available.</summary>
    public static string? PickUninstall(string displayName, string? quietUninstall, string? uninstall)
    {
        if (!string.IsNullOrWhiteSpace(quietUninstall)) return quietUninstall;
        if (!string.IsNullOrWhiteSpace(uninstall)) return uninstall;
        return $"winget uninstall \"{displayName}\" --silent";   // no registry string at all: last resort
    }

    // ---------- dispatch ----------

    public async Task<(bool Ok, string Detail)> RunAsync(Job job, CancellationToken ct)
    {
        try
        {
            return job.Kind switch
            {
                "update_game" => await UpdateGameAsync(job, ct),
                "cleanup_duplicate" => CleanupDuplicate(job),
                "remove_game" => RemoveGame(job),
                "remove_software" => RemoveSoftware(job),
                "deploy_content" => await EnsureContentAsync(job.Subject, ct),
                "install_app" or "update_app" => await InstallAppAsync(job, ct),
                _ => (false, $"unknown job kind '{job.Kind}' — refused"),
            };
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return (false, e.Message);
        }
    }

    // ---------- handlers ----------

    private async Task<(bool, string)> UpdateGameAsync(Job job, CancellationToken ct)
    {
        // T42: a null job path now means "install into the machine's default Steam library" —
        // master-list push/install onto stations that never had the game.
        var dir = !string.IsNullOrWhiteSpace(job.Path) ? job.Path! : GameScanner.DefaultSteamRoot();
        if (string.IsNullOrWhiteSpace(dir))
            return (false, "no install path in job and no default Steam library found on this machine");
        var args = SteamCmdArgs(dir,
                               job.Arg ?? throw new InvalidOperationException("missing app id"));
        // Prefer the discovered steamcmd (config path → winget link/package → PATH); fall back to
        // the bare config name only when nothing was located (job fails fast with exit != 0 then).
        var exe = SteamCmdWatch.Last is { Found: true, Path: { } sc } ? sc : cfg.SteamCmdPath;
        var exit = await RunProcessAsync(exe, args, TimeSpan.FromMinutes(cfg.JobTimeoutMin), ct);
        if (exit != 0) return (false, $"steamcmd exit {exit}");
        return (true, $"app_update {job.Arg} validate → exit 0 in {dir}");
    }

    private (bool, string) CleanupDuplicate(Job job)
    {
        var keep = job.Path ?? throw new InvalidOperationException("missing keep-path");
        var roots = GameScanner.EffectiveScanDirs(cfg.ScanDirs, GameScanner.SteamScanRoots());
        var scan = GameScanner.Scan(roots);                    // fresh truth, not the server's stale copy list
        var current = scan.Reports.Where(g => g.GameId == job.Subject).Select(g => g.InstallPath).ToList();
        var doomed = DuplicateCleanupPlan(current, keep);
        var removed = 0;
        foreach (var p in doomed)
        {
            if (!roots.Any(root => Path.GetFullPath(p).StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"refusing to delete outside scan roots: {p}");
            Directory.Delete(p, recursive: true);
            removed++;
        }
        return (true, $"removed {removed} duplicate copy(ies) of {job.Subject}, kept {keep}");
    }

    // T42: master-list "uninstall" — delete EVERY copy of the game (fresh scan, never the server's
    // stale copy list), including duplicate copies. Same roots guard as cleanup_duplicate: we only
    // ever delete inside scan roots, never a root itself. Idempotent: not installed = success.
    // Steam's appmanifest is left alone — a later app_update re-downloads missing files.
    public static List<string> RemoveGamePlan(IEnumerable<GameReport> current, string gameId) =>
        current.Where(g => g.GameId.Equals(gameId, StringComparison.OrdinalIgnoreCase))
               .Select(g => g.InstallPath).ToList();

    private (bool, string) RemoveGame(Job job)
    {
        var roots = GameScanner.EffectiveScanDirs(cfg.ScanDirs, GameScanner.SteamScanRoots());
        var scan = GameScanner.Scan(roots);
        var doomed = RemoveGamePlan(scan.Reports, job.Subject);
        if (doomed.Count == 0) return (true, $"{job.Subject} not installed here — nothing to do");
        var removed = 0;
        foreach (var p in doomed)
        {
            var full = Path.GetFullPath(p);
            if (!roots.Any(root => full.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
                || roots.Any(root => string.Equals(full, Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"refusing to delete scan root or out-of-root path: {p}");
            Directory.Delete(p, recursive: true);
            removed++;
        }
        return (true, $"removed {removed} copy(ies) of {job.Subject}");
    }

    private (bool, string) RemoveSoftware(Job job)
    {
        if (!OperatingSystem.IsWindows())
            return (false, "remove_software requires Windows (Uninstall-string inventory)");
        return RemoveSoftwareWindows(job);
    }

    [SupportedOSPlatform("windows")]
    private (bool, string) RemoveSoftwareWindows(Job job)
    {
        // Re-resolve from the live registry inventory; the job names an entry by display_name only.
        var rec = Core.StateModule.Record();
        if (rec["installedApps"] is not JsonArray apps) return (false, "no installedApps inventory");
        var entry = apps.FirstOrDefault(a => a?["display_name"]?.GetValue<string>() == job.Subject);
        if (entry is null) return (true, $"{job.Subject} not installed — nothing to do");
        var cmd = PickUninstall(job.Subject, entry["quiet_uninstall_string"]?.GetValue<string>(),
                                             entry["uninstall_string"]?.GetValue<string>());
        if (cmd is null) return (false, "no uninstall string available");
        // Guard re-check: even though the server vetoed protected apps, the agent refuses protected-named
        // core system entries outright as a second line of defense.
        if (job.Subject.Equals("Windows", StringComparison.OrdinalIgnoreCase))
            return (false, "protected by agent veto");
        var exit = RunProcessAsync("cmd.exe", ["/c", cmd], TimeSpan.FromMinutes(cfg.JobTimeoutMin),
                                   CancellationToken.None).GetAwaiter().GetResult();
        return exit == 0 ? (true, $"uninstalled {job.Subject}") : (false, $"uninstaller exit {exit}");
    }

    /// <summary>Idempotent content fetch: cached file with matching sha256 = no-op.
    /// Success detail = absolute cache file path; failure detail = reason.</summary>
    public async Task<(bool Ok, string PathOrError)> EnsureContentAsync(string contentId, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await http.GetStringAsync($"{cfg.ServerUrl}/api/content", ct));
        var itemEl = doc.RootElement.GetProperty("items").EnumerateArray()
            .FirstOrDefault(i => i.GetProperty("content_id").GetString() == contentId);
        if (itemEl.ValueKind != JsonValueKind.Object)
            return (false, $"content '{contentId}' not in server library");
        var item = Wire.FromJson<ContentItem>(itemEl.GetRawText())!;
        var cache = new ContentCache(http, ContentCacheDir);
        try
        {
            await cache.EnsureAsync(cfg.ServerUrl, item, ct);   // idempotent: sha256 match skips download
            return (true, Path.Combine(ContentCacheDir, item.ContentId, item.Filename));
        }
        catch (InvalidDataException e) { return (false, e.Message); }
        catch (HttpRequestException e) { return (false, $"content fetch failed: {e.Message}"); }
    }

    // ---------- job state reporting (best-effort: agent keeps looping even if a report is lost) ----------

    public async Task ReportJobStateAsync(Job job, string state, string? detail, CancellationToken ct = default, int? progress = null)
    {
        try
        {
            await http.PostAsync($"{cfg.ServerUrl}/api/job_update",
                new StringContent(Wire.ToJson(new JobUpdateRequest(job.JobId, job.ClientId, state, detail, progress)), System.Text.Encoding.UTF8, "application/json"), ct);
        }
        catch (HttpRequestException) { /* server will re-dispatch or time the job out */ }
    }

    // ---------- venue app installs (T29): direct vendor download + progress + silent run ----------

    /// <summary>Stream a vendor installer to disk, reporting throttled progress onto the job.
    /// Unknown Content-Length (redirect-based vendors) → null percent: job still runs, the bar
    /// just renders indeterminate on the dashboard.</summary>
    public async Task<(bool Ok, string? Error)> DownloadWithProgressAsync(string url, string file, Job job, CancellationToken ct)
    {
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return (false, "installer URL is not https — refused");
        try
        {
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode) return (false, $"download failed: HTTP {(int)resp.StatusCode}");
            long total = resp.Content.Headers.ContentLength ?? -1;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(file);
            var buf = new byte[64 * 1024];
            long done = 0, sinceReport = 0;
            var lastReport = DateTime.MinValue;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n; sinceReport += n;
                var pct = AppCatalog.ProgressPercent(done, total);
                if (sinceReport >= 512_000 && (DateTime.UtcNow - lastReport).TotalSeconds >= 1)
                {
                    await ReportJobStateAsync(job, "running", $"{done / (1024 * 1024)} MB downloaded", ct, pct);
                    sinceReport = 0; lastReport = DateTime.UtcNow;
                }
            }
            await ReportJobStateAsync(job, "running", $"{done / (1024 * 1024)} MB downloaded", ct,
                AppCatalog.ProgressPercent(done, total) ?? 100);
            return (true, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (HttpRequestException e) { return (false, $"download failed: {e.Message}"); }
        catch (IOException e) { return (false, $"download write failed: {e.Message}"); }
    }

    /// <summary>install_app / update_app share this flow: vendor "always latest" installers upgrade
    /// in place, so re-running the installer is the update. MSI URLs run through msiexec; the rest
    /// execute directly with the catalog's silent switches.</summary>
    private async Task<(bool Ok, string Detail)> InstallAppAsync(Job job, CancellationToken ct)
    {
        var entry = AppCatalog.Entry(job.Subject);
        if (entry is null) return (false, $"app '{job.Subject}' not in venue catalog — refused");

        var dir = Path.Combine(cfg.DataDir, "downloads");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, entry.Id + (AppCatalog.IsMsiUrl(entry.InstallerUrl) ? ".msi" : ".exe"));

        var (ok, err) = await DownloadWithProgressAsync(entry.InstallerUrl, file, job, ct);
        if (!ok) return (false, $"{entry.Name}: {err}");

        await ReportJobStateAsync(job, "running", $"installing {entry.Name}…", ct);
        var msi = AppCatalog.IsMsiUrl(entry.InstallerUrl);
        var exe = msi ? "msiexec.exe" : file;
        var args = msi
            ? new List<string> { "/i", file, entry.Args, "/norestart" }.Where(a => a.Length > 0).ToList()
            : entry.Args.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        int exit; string? runErr = null;
        try { exit = await RunProcessAsync(exe, args, TimeSpan.FromMinutes(cfg.JobTimeoutMin), ct); }
        catch (Exception e) { runErr = e.Message; exit = -1; }
        try { File.Delete(file); } catch (IOException) { }   // vendor installer is regenerable — never litter the machine with it
        if (runErr != null) return (false, $"{entry.Name} installer could not run: {runErr}");
        if (exit != 0) return (false, $"{entry.Name} installer exited {exit}");

        AppVersionCache.Invalidate();   // next beat refetches latest + re-projects the apps node
        return (true, $"{entry.Name} installed/updated (direct vendor download)");
    }

    // ---------- process helper (timeout + tree kill) ----------

    private static async Task<int> RunProcessAsync(string exe, List<string> args, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo { FileName = exe, UseShellExecute = false,
                                         CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);   // argv passed verbatim — no shell string concatenation
        using var p = new Process { StartInfo = psi };
        p.Start();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try { await p.WaitForExitAsync(cts.Token); return p.ExitCode; }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new TimeoutException($"timed out after {timeout.TotalMinutes:0} min");
        }
    }
}
