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
        return ack?.Ok ?? false;
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
    /// <summary>Locate overlay surface. Default spawns the same-EXE overlay child; tests inject a probe.</summary>
    public Action<LocateOverlayRequest> OverlaySurface { get; set; } = req => OverlayLauncher.Launch(req);
    private string? _lastHash;
    private string? _downedContentApplied;          // content_id of the downed wallpaper currently applied
    private DateTime _reportNow = DateTime.MinValue;

    public async Task RunAsync(CancellationToken ct)
    {
        var id = Identity.LoadOrCreate(cfg.DataDir);
        var client = new AgentClient(http, cfg, id);
        var runner = new JobRunner(http, cfg);
        Console.WriteLine($"esportsutil agent {AgentInfo.Version} — client_id={id.ClientId} pin={id.DevicePin}");

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
                }

                if (registered)
                {
                    var ack = await client.HeartbeatAsync(AgentInfo.Version, ct);
                    if (ack is null || !ack.Ok)
                    {
                        if (ack?.Error == "unknown client_id — re-register") registered = false;
                    }
                    else
                    {
                        LastDesiredState = ack.DesiredState;
                        await ReconcileDownedAsync(ack, runner, ct);
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
                            if (ok) _reportNow = DateTime.UtcNow;      // prove the repair: re-report promptly
                        }
                        if (DateTime.UtcNow >= nextReport || DateTime.UtcNow >= _reportNow)
                        {
                            var m = await client.BuildManifestAsync(ct);
                            var hash = AgentClient.ContentHash(m);
                            if (hash != _lastHash && await client.ReportAsync(m, ct)) { _lastHash = hash; ReportsSent++; }
                            nextReport = DateTime.UtcNow.AddMinutes(cfg.SlowScanIntervalMin);
                            _reportNow = DateTime.MaxValue;
                        }
                    }
                }
            }
            catch (HttpRequestException) { registered = false; }              // server away; retry next tick
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
}
