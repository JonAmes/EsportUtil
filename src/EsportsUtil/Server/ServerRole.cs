using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using EsportsUtil.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EsportsUtil.Server;

/// <summary>The server role: ASP.NET Core minimal API + E-Sports Dashboard on one port.
/// Endpoints mirror docs/data_contracts.md — every client-facing call returns the same AckEnvelope.</summary>
public static class ServerRole
{
    public static WebApplication BuildApp(EsportsConfig cfg, ILoggerFactory? logs = null)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = Array.Empty<string>(),
            EnvironmentName = Environments.Production,
        });
        if (logs is not null) builder.Services.AddSingleton(logs);
        else builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(cfg.ServerUrl);

        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
            o.SerializerOptions.PropertyNameCaseInsensitive = true;
            o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        });

        var registry = new DeviceRegistry(cfg.DataDir);
        var manifests = new ManifestStore(cfg.DataDir);
        var content = new ContentLibrary(cfg.DataDir);
        var hub = new AlertHub();
        var labelsDir = Path.Combine(cfg.DataDir, "labels");
        var printer = new FileLabelPrinter(labelsDir);
        const string RevokedMsg = "offboarded by operator — identity revoked";
        var issueHashes = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
        var jobs = new JobStore(cfg.DataDir);

        builder.Services.AddHostedService(_ => new LivenessMonitor(registry, hub, cfg.HeartbeatTimeoutSec));

        // Phase 3 — hosted notification daemon drains the queue; Windows toast on the server box, console on dev.
        INotifier notifier =
#if WINDOWS
            OperatingSystem.IsWindows() && cfg.ToastNotifications ? new WindowsToastNotifier(cfg.ServerUrl) :
#endif
            new ConsoleNotifier();
        builder.Services.AddHostedService(sp => new NotificationService(hub, notifier,
            sp.GetRequiredService<ILoggerFactory>().CreateLogger("esportsutil.notify")));
        builder.Services.AddSingleton(hub);

        var app = builder.Build();
        var log = (logs ?? app.Services.GetRequiredService<ILoggerFactory>()).CreateLogger("esportsutil.server");

        // ---- onboarding gate ----
        app.MapPost("/api/register", (RegisterRequest req) =>
        {
            if (registry.IsRevoked(req.ClientId))
                return Results.Json(new AckEnvelope(false, RevokedMsg), Wire.Options, statusCode: 403);
            if (!registry.TryRegister(req.ClientId, req.DevicePin, req.MachineModel, out var e))
                return Results.Json(new AckEnvelope(false, "invalid or missing device_pin"), Wire.Options, statusCode: 400);
            if (e.Pending)
                _ = hub.PublishAsync(new Alert("pairing_requested", "info",
                    "awaiting operator name", e.ClientId, e.DevicePin, DateTimeOffset.UtcNow));
            return Results.Json(new AckEnvelope(true, DesiredState: e.DesiredState, DownedContentId: e.DownedContentId), Wire.Options);
        });

        // ---- heartbeat: ack carries desired state, unexpired directives, and the next queued job ----
        app.MapPost("/api/heartbeat", (HeartbeatRequest req) =>
        {
            if (registry.IsRevoked(req.ClientId))
                return Results.Json(new AckEnvelope(false, RevokedMsg), Wire.Options, statusCode: 410);
            if (!registry.Heartbeat(req.ClientId, out var e))
                return Results.Json(new AckEnvelope(false, "unknown client_id — re-register"), Wire.Options, statusCode: 404);
            jobs.FailAbandoned(cfg.JobTimeoutMin);   // zombie-job sweep, cheap list scan
            return Results.Json(new AckEnvelope(true,
                DesiredState: e.DesiredState,
                DownedContentId: e.DownedContentId,
                Directives: registry.TakeDirectives(req.ClientId),
                Job: jobs.NextQueued(req.ClientId),
                DisplayName: e.Pending ? null : e.DisplayName,
                DownedReason: e.DownedReason), Wire.Options);
        });

        // ---- Phase 4: shared per-client issue resolution (drives /api/state AND job-creation guards) ----
        static List<Issue> IssuesFor(LocalManifest lm, MasterManifest master, ApprovedSoftwarePolicy? policy) =>
            Comparator.Games(lm, master).Concat(Comparator.Software(lm, policy)).ToList();

        (LocalManifest rep, List<Issue> issues)? DeviceState(string clientId)
        {
            var f = Path.Combine(manifests.ReportsDir, clientId + ".json");
            if (!File.Exists(f)) return null;
            var lm = Wire.FromJson<LocalManifest>(File.ReadAllText(f));
            if (lm is null) return null;
            return (lm, IssuesFor(lm, manifests.LoadMaster(), manifests.LoadPolicy()));
        }

        // ---- local manifest report; content-hash dedupe so unchanged states cost ~zero ----
        app.MapPost("/api/report", (LocalManifest report) =>
        {
            if (registry.IsRevoked(report.ClientId))
                return Results.Json(new AckEnvelope(false, RevokedMsg), Wire.Options, statusCode: 410);
            if (!registry.Exists(report.ClientId))
                return Results.Json(new AckEnvelope(false, "unknown client_id — re-register"), Wire.Options, statusCode: 404);
            var hash = ReportHash.Of(report);
            if (manifests.Summaries.TryGetValue(report.ClientId, out var prev) && prev.ContentHash == hash)
                return Results.Json(new AckEnvelope(true, Error: null, "unchanged"), Wire.Options);
            manifests.SaveReport(report, hash);
            registry.UpdateModel(report.ClientId, report.MachineState?["machineModel"]?.GetValue<string>());
            return Results.Json(new AckEnvelope(true), Wire.Options);
        });

        // ---- factory state pull ----
        app.MapGet("/api/factory-state", () => Results.Json(manifests.LoadMaster().FactoryStateMap, Wire.Options));

        // ---- operator controls (dashboard) ----
        app.MapPost("/api/rename", (RenameRequest req) =>
            registry.Rename(req.ClientId, req.DisplayName, req.Location)
                ? Results.Json(new AckEnvelope(true), Wire.Options)
                : Results.Json(new AckEnvelope(false, "unknown device or invalid name"), Wire.Options, statusCode: 400));

        app.MapPost("/api/downed", (DownedRequest req) =>
            registry.SetDowned(req.ClientId, req.DesiredState, req.DownedContentId, req.DownedReason)
                ? Results.Json(new AckEnvelope(true), Wire.Options)
                : Results.Json(new AckEnvelope(false, "unknown device or invalid state"), Wire.Options, statusCode: 400));

        // Offboard: operator removes a PC and revokes its PIN/identity. The client_id is tombstoned
        // (revoked.json) so it can never re-register; a live agent rotates identity and re-enters
        // as a pending pairing, gated by the operator again. Report/history files stay for forensics.
        app.MapPost("/api/offboard/{clientId}", (string clientId) =>
        {
            var e = registry.Snapshot().FirstOrDefault(x => x.ClientId == clientId);
            if (!registry.Offboard(clientId))
                return Results.Json(new AckEnvelope(false, "unknown device"), Wire.Options, statusCode: 404);
            _ = hub.PublishAsync(new Alert("offboarded", "warn",
                $@"PIN revoked — {(string.IsNullOrWhiteSpace(e!.DisplayName) ? "unpaired device" : e.DisplayName)} ({e.DevicePin})", clientId, null, DateTimeOffset.UtcNow));
            return Results.Json(new AckEnvelope(true), Wire.Options);
        });

        app.MapPost("/api/locate/{clientId}", (string clientId) =>
            registry.QueueDirective(clientId, new Directive("locate", registry.DisplayNameOf(clientId),
                DateTimeOffset.UtcNow.ToUnixTimeSeconds() + cfg.LocateTtlSec))
                ? Results.Json(new AckEnvelope(true), Wire.Options)
                : Results.Json(new AckEnvelope(false, "unknown device"), Wire.Options, statusCode: 404));

        app.MapPost("/api/print-label/{clientId}", (string clientId) =>
        {
            var e = registry.Snapshot().FirstOrDefault(x => x.ClientId == clientId);
            if (e is null) return Results.Json(new AckEnvelope(false, "unknown device"), Wire.Options, statusCode: 404);
            if (e.Pending) return Results.Json(new AckEnvelope(false, "name this device (and set its location) before printing its label"), Wire.Options, statusCode: 400);
            var path = printer.Print(e.DisplayName, e.Location);
            return Results.Json(new { ok = true, artifact = path, url = "/labels/" + Path.GetFileName(path) }, Wire.Options);
        });

        // Serve generated label artifacts (filenames are server-made slugs; validate to be safe).
        app.MapGet("/labels/{file}", (string file) =>
        {
            if (!file.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
                || !file.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
                return Results.NotFound();
            var path = Path.Combine(labelsDir, file);
            return System.IO.File.Exists(path) ? Results.File(path, "text/html") : Results.NotFound();
        });

        // ---- content library ----
        app.MapGet("/api/content", () => Results.Json(content.Load(), Wire.Options));
        app.MapGet("/content/{contentId}", (string contentId) =>
        {
            var path = content.FilePathFor(contentId);
            return path is null ? Results.NotFound() : Results.File(path, "application/octet-stream");
        });

        // ---- Phase 4 jobs: creation guards, agent state reports ----
        app.MapPost("/api/jobs", (NewJobRequest req) =>
        {
            if (!registry.Exists(req.ClientId))
                return Results.Json(new AckEnvelope(false, "unknown device"), Wire.Options, statusCode: 404);
            if (DeviceState(req.ClientId) is not { } st)
                return Results.Json(new AckEnvelope(false, "device has never reported — nothing to remediate against"), Wire.Options, statusCode: 400);
            if (jobs.HasOpen(req.ClientId, req.Kind, req.Subject))
                return Results.Json(new AckEnvelope(false, "identical job already queued or running"), Wire.Options, statusCode: 409);

            var master = manifests.LoadMaster();
            string? arg = null, path = null;
            switch (req.Kind)
            {
                case "update_game":
                    // Allow-list rule: we only patch games the Master Manifest names, with a Steam app id.
                    var mg = master.MasterList.FirstOrDefault(g => g.GameId.Equals(req.Subject, StringComparison.OrdinalIgnoreCase));
                    if (mg is null) return Results.Json(new AckEnvelope(false, "game not in Master Manifest"), Wire.Options, statusCode: 400);
                    if (mg.AppId is null) return Results.Json(new AckEnvelope(false, "no Steam app_id in Master Manifest — update handler unsupported for this game"), Wire.Options, statusCode: 400);
                    if (st.issues.Any(i => i.Kind == "rollback_required" && i.Subject == req.Subject))
                        return Results.Json(new AckEnvelope(false, "SteamCMD cannot downgrade — apply the factory baseline instead"), Wire.Options, statusCode: 400);
                    if (!st.issues.Any(i => i.Subject == req.Subject && i.Kind is "update_required" or "missing_from_machine"))
                        return Results.Json(new AckEnvelope(false, "no update/missing issue for that game"), Wire.Options, statusCode: 400);
                    var iss = st.issues.First(i => i.Subject == req.Subject && i.Kind is "update_required" or "missing_from_machine");
                    path = iss.KeepPath
                        ?? master.FactoryStateMap.SelectMany(f => f.GameBaselines)
                           .FirstOrDefault(b => b.GameId.Equals(req.Subject, StringComparison.OrdinalIgnoreCase))?.InstallPath;
                    if (path is null) return Results.Json(new AckEnvelope(false, "no known install path for this game"), Wire.Options, statusCode: 400);
                    arg = mg.AppId;
                    break;
                case "cleanup_duplicate":
                    var dup = st.issues.FirstOrDefault(i => i.Kind == "duplicate_resolved" && i.Subject == req.Subject);
                    if (dup?.KeepPath is null)
                        return Results.Json(new AckEnvelope(false, "no server-resolved duplicate for that game"), Wire.Options, statusCode: 400);
                    path = dup.KeepPath;
                    break;
                case "remove_software":
                    var app = st.issues.FirstOrDefault(i => i.Kind == "unapproved_app" && i.Subject == req.Subject);
                    if (app is null) return Results.Json(new AckEnvelope(false, "no unapproved_app issue for that entry"), Wire.Options, statusCode: 400);
                    if (app.Protected) return Results.Json(new AckEnvelope(false, "entry is on the protected veto list"), Wire.Options, statusCode: 403);
                    break;
                case "deploy_content":
                    if (content.Find(req.Subject) is null)
                        return Results.Json(new AckEnvelope(false, "content_id not in library"), Wire.Options, statusCode: 400);
                    break;
                default:
                    return Results.Json(new AckEnvelope(false, $"unknown job kind '{req.Kind}'"), Wire.Options, statusCode: 400);
            }

            var job = jobs.Enqueue(req.ClientId, req.Kind, req.Subject, arg, path);
            var pin = registry.Snapshot().FirstOrDefault(x => x.ClientId == req.ClientId)?.DevicePin;
            _ = hub.PublishAsync(new Alert("job_queued", "info", $"{req.Kind} {req.Subject}",
                req.ClientId, pin, DateTimeOffset.UtcNow));
            return Results.Json(job, Wire.Options);
        });

        app.MapPost("/api/job_update", (JobUpdateRequest req) =>
        {
            var j = jobs.Update(req.JobId, req.ClientId, req.State, req.Detail);
            if (j is null)
                return Results.Json(new AckEnvelope(false, "unknown job, wrong client, or terminal state"), Wire.Options, statusCode: 400);
            if (j.State is "succeeded" or "failed")
            {
                var pin = registry.Snapshot().FirstOrDefault(x => x.ClientId == req.ClientId)?.DevicePin;
                _ = hub.PublishAsync(new Alert(j.State == "succeeded" ? "job_succeeded" : "job_failed",
                    j.State == "succeeded" ? "info" : "critical",
                    $"{j.Kind} {j.Subject}: {j.Detail}", req.ClientId, pin, DateTimeOffset.UtcNow));
            }
            return Results.Json(j, Wire.Options);
        });

        // ---- dashboard snapshot for the live E-Sports Dashboard ----
        app.MapGet("/api/state", () =>
        {
            var now = DateTimeOffset.UtcNow;
            var master = manifests.LoadMaster();
            var policy = manifests.LoadPolicy();

            var issuesByClient = new Dictionary<string, List<Issue>>();
            foreach (var f in Directory.GetFiles(manifests.ReportsDir, "*.json"))
                if (Wire.FromJson<LocalManifest>(File.ReadAllText(f)) is { } lm)
                    issuesByClient[lm.ClientId] = IssuesFor(lm, master, policy);

            var devices = registry.Snapshot().OrderBy(e => e.DevicePin).Select(e =>
            {
                manifests.Summaries.TryGetValue(e.ClientId, out var sum);
                var hbAgo = e.LastHeartbeat is null ? (double?)null : (now - e.LastHeartbeat.Value).TotalSeconds;
                var issues = issuesByClient.TryGetValue(e.ClientId, out var iss) ? iss : new List<Issue>();
                return new
                {
                    client_id = e.ClientId,
                    device_pin = e.DevicePin,
                    display_name = e.Pending ? $"#{e.DevicePin}" : e.DisplayName,
                    location = e.Location,
                    pending = e.Pending,
                    online = hbAgo is not null && hbAgo <= cfg.HeartbeatTimeoutSec,
                    last_heartbeat_sec_ago = hbAgo is null ? (double?)null : Math.Round(hbAgo.Value, 1),
                    desired_state = e.DesiredState,
                    downed_content_id = e.DownedContentId,
                    downed_reason = e.DownedReason,
                    machine_model = e.MachineModel,
                    games = sum?.GameCount,
                    duplicates = sum?.DuplicateCount,
                    agent_version = sum?.AgentVersion,
                    last_report = sum?.ReportTimestamp,
                    locate_pending = e.PendingDirectives.Any(d => d.ExpiresUnix > now.ToUnixTimeSeconds()),
                    issues,
                    jobs = jobs.ForClient(e.ClientId),
                };
            }).ToList();

            // Compliance alerting: publish once per device whenever its issue set changes.
            foreach (var (cid, issues) in issuesByClient)
            {
                var h = Comparator.Hash(issues);
                if (issueHashes.TryGetValue(cid, out var old) && old == h) continue;
                issueHashes[cid] = h;
                if (issues.Count == 0) continue;
                var pin = registry.Snapshot().FirstOrDefault(x => x.ClientId == cid)?.DevicePin;
                _ = hub.PublishAsync(new Alert("compliance", "warn",
                    string.Join(", ", issues.GroupBy(i => i.Kind).Select(g => $"{g.Key}×{g.Count()}")), cid, pin, now));
            }

            return Results.Json(new
            {
                server_time = now,
                manifest_version = master.Version,
                factory_entries = master.FactoryStateMap.Count,
                policy_active = policy is not null,
                agents_reporting = devices.Count(d => d.online),
                agents_total = devices.Count(),
                devices,
                alerts = hub.Recent().AsEnumerable().Reverse(),
                budgets = cfg.Budgets,
            }, Wire.Options);
        });

        // ---- dashboard HTML (served by the EXE, rendered in Edge) ----
        var dashPath = Path.Combine(AppContext.BaseDirectory, "wwwroot", "dashboard.html");
        app.MapGet("/", () => File.Exists(dashPath)
            ? Results.Content(File.ReadAllText(dashPath), "text/html")
            : Results.Text("dashboard.html missing next to esportsutil"));

        return app;
    }

    public static async Task RunAsync(EsportsConfig cfg, CancellationToken ct)
    {
        using var app = BuildApp(cfg);
        await app.StartAsync(ct);
        Console.WriteLine($"esportsutil server: {cfg.ServerUrl} (dashboard at /)");
        await app.WaitForShutdownAsync(ct);
    }
}
