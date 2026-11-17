using System.Text.Json.Nodes;
using EsportsUtil.Core;

namespace EsportsUtil.Agent;

public sealed record AgentIdentity(string ClientId, string DevicePin);

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
        var clientId = Guid.NewGuid().ToString();
        var seed = Environment.MachineName + "|" + (Environment.GetEnvironmentVariable("ESU_HW_SEED") ?? MachineSeed());
        var pin = DerivePin(seed);
        var idNew = new AgentIdentity(clientId, pin);
        File.WriteAllText(path, Wire.ToJson(idNew));
        return idNew;
    }

    /// <summary>Offboarded by the operator: burn the persisted client_id and mint a fresh one.
    /// The PIN is deterministically re-derived from the same machine seed, so the machine keeps
    /// its human-recognizable badge and re-enters pairing as a NEW pending device.</summary>
    public static AgentIdentity Rotate(string dataDir)
    {
        var path = Path.Combine(dataDir, "esportsutil.identity.json");
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        return LoadOrCreate(dataDir);
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
        var (games, dupes) = GameScanner.Scan(cfg.ScanDirs);
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

public static class AgentInfo { public const string Version = "0.1.0"; }

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
    private string? _lastHash;
    private string? _downedContentApplied;          // content_id of the downed wallpaper currently applied
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
                        await ReconcileDownedAsync(ack, runner, ct);
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
                                else
                                    Console.WriteLine($"directive '{d.Type}' received (expires {d.ExpiresUnix})");
                        }
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

    /// <summary>Downed convergence (desired-state, not liveness): while downed, ensure the downed
    /// wallpaper content is cached and applied; on return to active, restore the recorded baseline.
    /// Idempotent — only touches the desktop when the intended wallpaper actually changes.</summary>
    private async Task ReconcileDownedAsync(AckEnvelope ack, JobRunner runner, CancellationToken ct)
    {
        if (ack.DesiredState == "downed")
        {
            var contentId = ack.DownedContentId;
            if (contentId is null || contentId == _downedContentApplied) return;
            var (ok, pathOrErr) = await runner.EnsureContentAsync(contentId, ct);
            if (!ok) { Console.WriteLine($"downed wallpaper: {pathOrErr}"); return; }
            Wallpaper.Apply(pathOrErr, cfg.DataDir, contentId);
            _downedContentApplied = contentId;
            Console.WriteLine($"downed wallpaper applied ({contentId})");
        }
        else if (_downedContentApplied is not null)
        {
            Wallpaper.Restore(cfg.DataDir);
            _downedContentApplied = null;
            Console.WriteLine("downed cleared — baseline wallpaper restored");
        }
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
