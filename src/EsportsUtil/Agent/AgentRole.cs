using System.Diagnostics;
using System.Text.Json.Nodes;
using EsportsUtil.Core;

namespace EsportsUtil.Agent;

public sealed record AgentIdentity(string ClientId, string DevicePin, int RotateSalt = 0);

/// <summary>Agent identity: a persisted GUID client_id plus the immutable 4-digit DevicePin,
/// deterministically minted from machine identity (same rule on every OS).</summary>
public static class Identity
{
    public static AgentIdentity LoadOrCreate(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        var path = Path.Combine(dataDir, "esportsutil.identity.json");
        if (File.Exists(path))
        {
            var id = Wire.FromJson<AgentIdentity>(File.ReadAllText(path));
            if (id is not null) return id;
        }
        return Mint(dataDir, 0);
    }

    /// <summary>OFFBOARDING ONLY (403 'offboarded'): burn the tombstoned client_id and mint a new
    /// one. The PIN is deterministically re-derived from the same machine seed, so the machine keeps
    /// its human-recognizable badge and re-enters pairing as a NEW pending device. The salt advances
    /// so deterministic re-mints can never be permanently pinned by the tombstone.</summary>
    public static AgentIdentity Rotate(string dataDir)
    {
        var path = Path.Combine(dataDir, "esportsutil.identity.json");
        var salt = 1;
        try
        {
            if (File.Exists(path))
            {
                var old = Wire.FromJson<AgentIdentity>(File.ReadAllText(path));
                if (old is not null) salt = old.RotateSalt + 1;
                File.Delete(path);
            }
        }
        catch (IOException) { }
        return Mint(dataDir, salt);
    }

    /// <summary>T37: a MISSING identity file (data dir moved with the EXE, fresh install) re-mints
    /// the SAME client_id on the same machine — deterministic from machine name + hardware seed.
    /// Agent updates/folder moves are invisible to the server: the device is simply known again.
    /// Collisions need the same PC name AND same model — in a venue that means an image-clone bug.
    /// A persisted file always wins, so pre-T37 random ids and rotated ids stay untouched.</summary>
    private static AgentIdentity Mint(string dataDir, int salt)
    {
        var seed = Environment.MachineName + "|" + (Environment.GetEnvironmentVariable("ESU_HW_SEED") ?? MachineSeed());
        var id = new AgentIdentity(
            DeterministicId(salt == 0 ? seed : seed + "|rotate-" + salt),
            DerivePin(seed), salt);
        File.WriteAllText(Path.Combine(dataDir, "esportsutil.identity.json"), Wire.ToJson(id));
        return id;
    }

    public static string DeterministicId(string seed)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("esportsutil-client|" + seed));
        var b = new byte[16];
        System.Array.Copy(hash, b, 16);
        b[6] = (byte)((b[6] & 0x0F) | 0x50);   // name-based UUID shape
        b[8] = (byte)((b[8] & 0x3F) | 0x80);
        return new Guid(b).ToString();
    }

    private static string MachineSeed()
    {
        if (OperatingSystem.IsWindows())
            return StateModule.Record()["machineModel"]?.GetValue<string>() ?? Environment.MachineName;
        foreach (var p in new[] { "/sys/class/dmi/id/product_uuid", "/var/lib/dbus/machine-id", "/etc/machine-id" })
            try { if (File.Exists(p)) return File.ReadAllText(p).Trim(); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        return Environment.MachineName;
    }

    public static string DerivePin(string seed)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(seed));
        var n = ((hash[0] << 24) | (hash[1] << 16) | (hash[2] << 8) | hash[3]) & 0x7FFFFFFF;
        return (n % 10000).ToString("D4");
    }
}

/// <summary>Thin transport — every call mirrors docs/data_contracts.md. All state capture is delegated
/// to Core (StateModule/GameScanner); the transport never re-implements it (no-duplication rule).</summary>
public sealed class AgentClient(HttpClient http, EsportsConfig cfg, AgentIdentity id)
{
    /// <summary>Current identity — rotates when the operator offboards this machine (see Rotate).</summary>
    public AgentIdentity Id => id;

    private static async Task<AckEnvelope?> PostAckAsync(HttpClient http, string url, object body, CancellationToken ct)
    {
        var resp = await http.PostAsync(url,
            new StringContent(Wire.ToJson(body), System.Text.Encoding.UTF8, "application/json"), ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(text))
            return new AckEnvelope(false, $"http {(int)resp.StatusCode} (empty body)");
        return Wire.FromJson<AckEnvelope>(text)
            ?? new AckEnvelope(false, $"http {(int)resp.StatusCode}");
    }

    public async Task<bool> RegisterAsync(string? machineModel, CancellationToken ct = default)
    {
        var ack = await PostAckAsync(http, $"{cfg.ServerUrl}/api/register",
            new RegisterRequest(id.ClientId, id.DevicePin, machineModel), ct);
        if (ack?.Ok == true) return true;
        if (ack?.Error?.Contains("offboarded") == true)
        {
            id = Identity.Rotate(cfg.DataDir);
            Console.WriteLine("OPERATOR OFFBOARDED THIS MACHINE — new client_id minted; awaiting re-pairing");
        }
        return false;
    }

    public async Task<AckEnvelope?> HeartbeatAsync(string agentVersion, CancellationToken ct = default)
        => await PostAckAsync(http, $"{cfg.ServerUrl}/api/heartbeat",
            new HeartbeatRequest(id.ClientId, id.DevicePin, DateTimeOffset.UtcNow, agentVersion), ct);

    /// <summary>Scan + Record, then report only if content hash changed since the last accepted report.</summary>
    public Task<LocalManifest> BuildManifestAsync(CancellationToken ct = default)
    {
        var (games, dupes) = GameScanner.Scan(GameScanner.EffectiveScanDirs(cfg.ScanDirs, GameScanner.SteamScanRoots()));
        return Task.FromResult(new LocalManifest(id.ClientId, id.DevicePin, null, DateTimeOffset.UtcNow,
            AgentInfo.Version, StateModule.Record(), games, dupes.Count > 0 ? dupes : null));
    }

    public static string ContentHash(LocalManifest m) => ReportHash.Of(m);

    public async Task<bool> ReportAsync(LocalManifest m, CancellationToken ct = default)
    {
        var ack = await PostAckAsync(http, $"{cfg.ServerUrl}/api/report", m, ct);
        return ack?.Ok ?? false;
    }
}

public static class AgentInfo { public static string Version => SelfUpdateWatch.CurrentVersion; }

/// <summary>The agent loop: heartbeat on interval (tiny fixed payload) is the spine; reports/manifest run
/// on the slow timer or right after register — never per heartbeat. Directives and desired_state are
/// consumed here as data. Phase 4: downed state converges the wallpaper locally (content cache + SPI),
/// and one heartbeat-acked job at a time runs through the allow-listed JobRunner with state reported back.</summary>
public sealed class AgentLoop(HttpClient http, EsportsConfig cfg)
{
    public string LastDesiredState { get; private set; } = "active";
    public List<Directive> ConsumedDirectives { get; } = new();
    public int ReportsSent { get; private set; }

    // ---- live state projection for the localhost client view (read-only) ----
    public bool IsRegistered { get; private set; }
    public AgentIdentity? CurrentIdentity => _client?.Id;
    public DateTimeOffset? LastAckAt { get; private set; }
    public bool LastAckOk { get; private set; }
    public string? LastAckError { get; private set; }
    /// <summary>Operator's display_name, streamed on the heartbeat ack once the device is paired; null = still pending.</summary>
    public string? PairedDisplayName { get; private set; }
    public DateTimeOffset? LastReportAt { get; private set; }
    public string? LastReportHash { get; private set; }
    public LocalManifest? LastReport { get; private set; }
    /// <summary>Most-recent-first ring of jobs this agent ran, with final state stamped (capped).</summary>
    public IReadOnlyList<Job> RecentJobs => _recentJobs;
    private readonly List<Job> _recentJobs = new();
    private AgentClient? _client;
    /// <summary>Locate overlay surface. Default spawns the same-EXE overlay child; tests inject a probe.</summary>
    public Action<LocateOverlayRequest> OverlaySurface { get; set; } = req => OverlayLauncher.Launch(req);

    /// <summary>Sticky downed overlay seam (Phase 5): same probe pattern as OverlaySurface so tests
    /// can capture requests and fake the child lifetime. The overlay has no expiry — the loop
    /// supervises it: staff bypass = child exits, operator clear = the loop kills it.</summary>
    public Func<DownedOverlayRequest, IDownOverlayHandle?> DownOverlaySurface { get; set; } = req => DownOverlayLauncher.Launch(req);
    public string? LastDownedReason { get; private set; }
    private IDownOverlayHandle? _downOverlay;
    private bool _downOverlayDismissed;

    /// <summary>Update overlay seam (T33): same supervised-child pattern; the install owns its whole
    /// lifetime — killed on the terminal install state, never dismissable on the machine.</summary>
    public Func<UpdateOverlayRequest, IDownOverlayHandle?> UpdateOverlaySurface { get; set; } = req => UpdateOverlayLauncher.Launch(req);
    private IDownOverlayHandle? _updateOverlay;
    private bool _rebootArmed;       // Windows gate: reboot armed exactly once per install
    private string? _lastHash;
    private string? _wallpaperKey;   // desired wallpaper currently applied: content_id while downed, "venue@<sha>" for the venue upload
    private bool _forceSelfUpdate;   // self_update directive (T30): check GitHub on this beat
    private bool _forceWallpaper;    // sync_wallpaper directive: re-run convergence even if the key is unchanged
    private string? _steamCmdKey;    // last reported "found|install_state" verdict; flip forces a prompt report
    private bool _wpDriftShown;      // last drift verdict reported to the server (flip → force a report)
    private DateTime _reportNow = DateTime.MinValue;

    public async Task RunAsync(CancellationToken ct)
    {
        var id = Identity.LoadOrCreate(cfg.DataDir);
        var client = new AgentClient(http, cfg, id);
        _client = client;
        var runner = new JobRunner(http, cfg);
        // Console banner mirrors the client_app mockup identity: big PIN, server, liveness.
        var pinSpaced = string.Join(' ', id.DevicePin.ToCharArray());
        Console.WriteLine("┌─ E-SPORTS DASHBOARD · CLIENT AGENT ────────────────────");
        Console.WriteLine($"│  PIN        {pinSpaced}");
        Console.WriteLine($"│  server     {cfg.ServerUrl}");
        Console.WriteLine($"│  client_id  {id.ClientId}");
        Console.WriteLine($"│  liveness   heartbeat every {cfg.HeartbeatIntervalSec}s · v{AgentInfo.Version}");
        Console.WriteLine("└────────────────────────────────────────────────────────");

        var registered = false;
        var nextReport = DateTime.MinValue;
        string? model = null;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!registered)
                {
                    model = StateModule.Record()["machineModel"]?.GetValue<string>();
                    registered = await client.RegisterAsync(model, ct);
                    IsRegistered = registered;
                }

                if (registered)
                {
                    var ack = await client.HeartbeatAsync(AgentInfo.Version, ct);
                    LastAckAt = DateTimeOffset.UtcNow;
                    LastAckOk = ack?.Ok == true;
                    LastAckError = ack?.Error;
                    if (ack is null || !ack.Ok)
                    {
                        if (ack?.Error == "unknown client_id — re-register") registered = false;
                        else if (ack?.Error?.Contains("offboarded") == true)
                        {
                            registered = false;       // next register rotates identity (see RegisterAsync)
                            Console.WriteLine("heartbeat refused: offboarded by operator — re-pairing required");
                        }
                        IsRegistered = registered;
                    }
                    else
                    {
                        LastDesiredState = ack.DesiredState;
                        PairedDisplayName = ack.DisplayName;
                        LastDownedReason = ack.DownedReason;
                        await ReconcileWallpaperAsync(ack, runner, ct);
                        ReconcileDownOverlay(ack, id, model);
                        if (ack.Directives is { Count: > 0 })
                        {
                            ConsumedDirectives.AddRange(ack.Directives);
                            foreach (var d in ack.Directives)
                                if (d.Type == "locate")
                                {
                                    // Phase 4 locate overlay: transient render in the user session,
                                    // suppressed client-side too if the directive already expired.
                                    var req = OverlayLauncher.RequestFor(d, id, model);
                                    if (req.ShouldShow(DateTimeOffset.UtcNow.ToUnixTimeSeconds()))
                                    {
                                        OverlaySurface(req);
                                        Console.WriteLine($"locate directive → overlay spawned (dismiss by {req.ExpiresUnix})");
                                    }
                                    else
                                        Console.WriteLine($"locate directive expired in transit — overlay suppressed");
                                }
                                else if (d.Type == "scan_updates")
                                {
                                    UpdateWatch.RequestScan();
                                    Console.WriteLine("scan_updates directive → Windows Update poll requested");
                                }
                                else if (d.Type == "install_updates")
                                {
                                    UpdateWatch.RequestInstall();
                                    Console.WriteLine("install_updates directive → full Windows Update install requested (overlay up, station held downed)");
                                }
                                else if (d.Type == "self_update")
                                    _forceSelfUpdate = true;   // operator pushed "app update" — poll GitHub now
                                else if (d.Type == "sync_wallpaper")
                                {
                                    _forceWallpaper = true;   // next beat re-runs convergence: re-verify cache, re-apply
                                    Console.WriteLine("sync_wallpaper directive → desired wallpaper will be re-applied");
                                }
                                else
                                    Console.WriteLine($"directive '{d.Type}' received (expires {d.ExpiresUnix})");
                        }
                        // Windows Update polling: timed interval AND operator-requested directives.
                        // Fire-and-forget on its own task — a slow WUA search never stalls the
                        // heartbeat spine; a finished poll forces a prompt report so the summary
                        // (and the dashboard state pill) reflects it within seconds, not a slow tick.
                        if (UpdateWatch.Due(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), UpdateWatch.Last?.LastScanUnix,
                                cfg.UpdateScanIntervalMin, UpdateWatch.IsScanning, UpdateWatch.HasRequestedScan))
                            _ = UpdateWatch.ScanAsync(ct).ContinueWith(_ =>
                            {
                                var wu = UpdateWatch.Last;
                                var pend = wu?.PendingCount is int p ? $" ({p} pending)" : "";
                                Console.WriteLine($"windows-update poll → {wu?.Status}{pend}");
                                _reportNow = DateTime.UtcNow;
                            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
                        // Windows Update INSTALL (T33, operator directive only): full-screen overlay spans
                        // the install, every stage writes update-progress.json (renderer re-reads it) AND
                        // forces a prompt report so the dashboard chip streams done/total. On the terminal
                        // state the agent kills the overlay; a reboot happens ONLY if WUA demands one.
                        if (UpdateWatch.DueInstall(UpdateWatch.IsScanning, UpdateWatch.IsInstalling, UpdateWatch.HasRequestedInstall))
                        {
                            var progPath = UpdateOverlayRequest.ProgressPathFor(cfg.DataDir);
                            if (_updateOverlay is not { Alive: true })
                            {
                                var name = string.IsNullOrWhiteSpace(ack.DisplayName) ? $"PC {id.DevicePin}" : ack.DisplayName;
                                _updateOverlay = UpdateOverlaySurface(new UpdateOverlayRequest(name, id.DevicePin, id.ClientId, model, progPath));
                                UpdateOverlayRequest.WriteProgress(progPath, "running", 0, UpdateWatch.Last?.PendingCount ?? 0, false);
                            }
                            _ = UpdateWatch.InstallAsync(
                                () =>
                                {
                                    var wu = UpdateWatch.Last;
                                    UpdateOverlayRequest.WriteProgress(progPath, wu?.InstallState ?? "running",
                                        wu?.InstallDone ?? 0, wu?.InstallTotal ?? 0, wu?.RebootPending ?? false);
                                    _reportNow = DateTime.UtcNow;
                                }, ct).ContinueWith(_ =>
                                {
                                    var wu = UpdateWatch.Last;
                                    _reportNow = DateTime.UtcNow;
                                    if (wu?.InstallState is "done" or "failed")
                                    {
                                        _updateOverlay?.Kill();
                                        _updateOverlay = null;
                                        Console.WriteLine($"windows-update install {wu.InstallState}: {wu.InstallDone}/{wu.InstallTotal}" +
                                            (wu.InstallError is { Length: > 0 } err ? $" — {err}" : ""));
                                        // Operator decision: auto-reboot ONLY when WUA says a reboot is required.
                                        // 75 s grace so the terminal report lands on the dashboard before the box reboots.
                                        if (wu.InstallState == "done" && wu.RebootPending == true &&
                                            OperatingSystem.IsWindows() && !_rebootArmed)
                                        {
                                            _rebootArmed = true;
                                            Console.WriteLine("WUA verdict: reboot required — arming shutdown /r /t 75");
                                            try
                                            {
                                                Process.Start(new ProcessStartInfo("shutdown",
                                                    "/r /t 75 /c \"EsportsUtil: Windows Update install complete — reboot required\"")
                                                { UseShellExecute = false });
                                            }
                                            catch (Exception e2) { Console.WriteLine($"reboot arm failed: {e2.Message}"); }
                                        }
                                    }
                                }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
                        }
                        // Wallpaper drift: the desktop can move off the enforced image after we apply
                        // it (user changes wallpaper, applied file tampered). Evaluate every beat;
                        // a drift flip forces a prompt report so the dashboard sync pill appears
                        // (or clears) within seconds.
                        var wp = WallpaperWatch.Check(cfg.DataDir);
                        if ((wp?.Drift ?? false) != _wpDriftShown)
                        {
                            _wpDriftShown = wp?.Drift ?? false;
                            _reportNow = DateTime.UtcNow;
                            Console.WriteLine(_wpDriftShown
                                ? "wallpaper drift — desktop no longer matches the enforced image"
                                : "wallpaper back in sync (or target removed)");
                        }
                        // SteamCMD presence + winget bootstrap (operator request): a missing
                        // steamcmd is repaired in the background by winget (never stalls the
                        // spine); verdict flips force a prompt report so steamCmd lands in the
                        // registry promptly.
                        var scm = await SteamCmdWatch.CheckAsync(cfg, ct);
                        var scmKey = $"{scm.Found}|{scm.InstallState}";
                        if (scmKey != _steamCmdKey)
                        {
                            _steamCmdKey = scmKey;
                            _reportNow = DateTime.UtcNow;
                        }
                        // Venue-app up-to-date model (T29): refresh the two vendor version probes
                        // when their 12h cache ages out (fire-and-forget; apps node projects it).
                        AppVersionCache.RefreshIfStale(http);
                        // Self-update (T30): GitHub release cadence check; download+verify+handoff
                        // runs fire-and-forget — the heartbeat never waits on GitHub.
                        var suNow = _forceSelfUpdate; _forceSelfUpdate = false;
                        await SelfUpdateWatch.CheckAsync(http, cfg, ct, force: suNow);
                        if (ack.Job is { } job)
                        {
                            Console.WriteLine($"job {job.Kind} '{job.Subject}' started ({job.JobId[..8]})");
                            await runner.ReportJobStateAsync(job, "running", null, ct);
                            var (ok, detail) = await runner.RunAsync(job, ct);
                            await runner.ReportJobStateAsync(job, ok ? "succeeded" : "failed", detail, ct);
                            Console.WriteLine($"job {job.Kind} '{job.Subject}' → {(ok ? "succeeded" : "failed")}: {detail}");
                            _recentJobs.Insert(0, job with
                            {
                                State = ok ? "succeeded" : "failed",
                                Detail = detail,
                                FinishedUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                            });
                            if (_recentJobs.Count > 20) _recentJobs.RemoveAt(_recentJobs.Count - 1);
                            if (ok) _reportNow = DateTime.UtcNow;      // prove the repair: re-report promptly
                        }
                        if (DateTime.UtcNow >= nextReport || DateTime.UtcNow >= _reportNow)
                        {
                            var m = await client.BuildManifestAsync(ct);
                            var hash = AgentClient.ContentHash(m);
                            if (hash != _lastHash && await client.ReportAsync(m, ct))
                            {
                                _lastHash = hash; ReportsSent++;
                                LastReport = m; LastReportAt = DateTimeOffset.UtcNow; LastReportHash = hash;
                            }
                            nextReport = DateTime.UtcNow.AddMinutes(cfg.SlowScanIntervalMin);
                            _reportNow = DateTime.MaxValue;
                        }
                    }
                }
            }
            catch (HttpRequestException) { registered = false; IsRegistered = false; }   // server away; retry next tick
            catch (OperationCanceledException) { break; }

            try { await Task.Delay(TimeSpan.FromSeconds(cfg.HeartbeatIntervalSec), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Wallpaper convergence (desired-state, not liveness) — one unified loop for both
    /// venue-surface images: while downed, the target is the downed marker (DownedContentId);
    /// otherwise the venue wallpaper when the operator has set one via the settings page
    /// (Content Library item `venue-wallpaper`, sha-keyed so a re-upload re-applies); otherwise
    /// none, which restores the baseline recorded on the first apply. Downed layers over the venue
    /// wallpaper and falls back to it on return to active. Idempotent — the desktop is touched only
    /// when the intended target actually changes.</summary>
    private async Task ReconcileWallpaperAsync(AckEnvelope ack, JobRunner runner, CancellationToken ct)
    {
        string? contentId = null, key = null;
        if (ack.DesiredState == "downed") { contentId = ack.DownedContentId; key = contentId; }
        else if (ack.VenueWallpaperSha256 is { } sha) { contentId = ContentLibrary.ContentIds.VenueWallpaper; key = "venue@" + sha; }

        if (key is null)
        {
            if (_wallpaperKey is not null)
            {
                Wallpaper.Restore(cfg.DataDir);
                _wallpaperKey = null;
                WallpaperWatch.SetExpected(null);
                Console.WriteLine("wallpaper: no target — baseline restored");
            }
            return;
        }
        if (key == _wallpaperKey && !_forceWallpaper) return;
        _forceWallpaper = false;
        var (ok, pathOrErr) = await runner.EnsureContentAsync(contentId!, ct);
        if (!ok) { Console.WriteLine($"wallpaper: {pathOrErr}"); return; }
        Wallpaper.Apply(pathOrErr, cfg.DataDir, contentId);
        _wallpaperKey = key;
        // sha recorded at apply time — the cache file was just verified against the server
        // manifest by EnsureContent, so this is the authoritative "what the desktop should show".
        WallpaperWatch.SetExpected(Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(pathOrErr))).ToLowerInvariant());
        Console.WriteLine($"wallpaper applied: {key}");
    }

    /// <summary>Sticky downed overlay lifecycle (no expiration): shown once per downed episode,
    /// never auto-dismisses. A staff bypass on the machine closes the child (detected via the handle);
    /// it is NOT re-shown for the rest of that episode — only a flip to active (which also kills a
    /// live overlay) and back to downed starts a new episode. Agent restart = new episode: the
    /// overlay comes back up, which is the point of a no-expiry marker on a rebooted dead PC.</summary>
    private void ReconcileDownOverlay(AckEnvelope ack, AgentIdentity id, string? model)
    {
        if (ack.DesiredState == "downed")
        {
            // An in-flight update install owns the screen — one card at a time; the install overlay
            // (which also holds the station downed server-side) outranks the plain downed card.
            if (_updateOverlay is { Alive: true }) return;
            if (_downOverlay is { } h)
            {
                if (!h.Alive)
                {
                    _downOverlay = null;
                    _downOverlayDismissed = true;
                    Console.WriteLine("downed overlay bypassed by staff — machine stays downed; no re-show until the operator flips it active");
                }
                return;
            }
            if (_downOverlayDismissed) return;
            var name = string.IsNullOrWhiteSpace(ack.DisplayName) ? $"PC {id.DevicePin}" : ack.DisplayName;
            _downOverlay = DownOverlaySurface(new DownedOverlayRequest(name, id.DevicePin, id.ClientId, model, ack.DownedReason));
            if (_downOverlay is null) _downOverlayDismissed = true;   // spawn failed — no hot retry every beat
            else Console.WriteLine($"downed overlay up — no expiry (bypass: B key or BYPASS chip) — note: {ack.DownedReason ?? "n/a"}");
        }
        else
        {
            if (_downOverlay is { } h)
            {
                if (h.Alive) { h.Kill(); Console.WriteLine("downed overlay cleared — operator returned machine to active"); }
                _downOverlay = null;
            }
            _downOverlayDismissed = false;
        }
    }
}
