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
        var printer = new FileLabelPrinter(Path.Combine(cfg.DataDir, "labels"));
        var issueHashes = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();

        builder.Services.AddHostedService(_ => new LivenessMonitor(registry, hub, cfg.HeartbeatTimeoutSec));
        builder.Services.AddSingleton(hub);

        var app = builder.Build();
        var log = (logs ?? app.Services.GetRequiredService<ILoggerFactory>()).CreateLogger("esportsutil.server");
        _ = hub.ConsumeAsync(a => log.LogWarning("[alert] {sev} {type} {dev} ({pin}): {msg}",
            a.Severity, a.Type, a.ClientId, a.DevicePin, a.Message), app.Lifetime.ApplicationStopping);

        // ---- onboarding gate ----
        app.MapPost("/api/register", (RegisterRequest req) =>
        {
            if (!registry.TryRegister(req.ClientId, req.DevicePin, req.MachineModel, out var e))
                return Results.Json(new AckEnvelope(false, "invalid or missing device_pin"), Wire.Options, statusCode: 400);
            if (e.Pending)
                _ = hub.PublishAsync(new Alert("pairing_requested", "info",
                    "awaiting operator name", e.ClientId, e.DevicePin, DateTimeOffset.UtcNow));
            return Results.Json(new AckEnvelope(true, DesiredState: e.DesiredState, DownedContentId: e.DownedContentId), Wire.Options);
        });

        // ---- heartbeat: ack carries desired state + any unexpired one-shot directives ----
        app.MapPost("/api/heartbeat", (HeartbeatRequest req) =>
        {
            if (!registry.Heartbeat(req.ClientId, out var e))
                return Results.Json(new AckEnvelope(false, "unknown client_id — re-register"), Wire.Options, statusCode: 404);
            return Results.Json(new AckEnvelope(true,
                DesiredState: e.DesiredState,
                DownedContentId: e.DownedContentId,
                Directives: registry.TakeDirectives(req.ClientId)), Wire.Options);
        });

        // ---- local manifest report; content-hash dedupe so unchanged states cost ~zero ----
        app.MapPost("/api/report", (LocalManifest report) =>
        {
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
            registry.Rename(req.ClientId, req.DisplayName)
                ? Results.Json(new AckEnvelope(true), Wire.Options)
                : Results.Json(new AckEnvelope(false, "unknown device or invalid name"), Wire.Options, statusCode: 400));

        app.MapPost("/api/downed", (DownedRequest req) =>
            registry.SetDowned(req.ClientId, req.DesiredState, req.DownedContentId, req.DownedReason)
                ? Results.Json(new AckEnvelope(true), Wire.Options)
                : Results.Json(new AckEnvelope(false, "unknown device or invalid state"), Wire.Options, statusCode: 400));

        app.MapPost("/api/locate/{clientId}", (string clientId) =>
            registry.QueueDirective(clientId, new Directive("locate", null,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds() + cfg.LocateTtlSec))
                ? Results.Json(new AckEnvelope(true), Wire.Options)
                : Results.Json(new AckEnvelope(false, "unknown device"), Wire.Options, statusCode: 404));

        app.MapPost("/api/print-label/{clientId}", (string clientId) =>
        {
            var e = registry.Snapshot().FirstOrDefault(x => x.ClientId == clientId);
            if (e is null) return Results.Json(new AckEnvelope(false, "unknown device"), Wire.Options, statusCode: 404);
            var path = printer.Print(string.IsNullOrEmpty(e.DisplayName) ? e.DevicePin : e.DisplayName, e.DevicePin, e.ClientId);
            return Results.Json(new { ok = true, artifact = path }, Wire.Options);
        });

        // ---- content library ----
        app.MapGet("/api/content", () => Results.Json(content.Load(), Wire.Options));
        app.MapGet("/content/{contentId}", (string contentId) =>
        {
            var path = content.FilePathFor(contentId);
            return path is null ? Results.NotFound() : Results.File(path, "application/octet-stream");
        });

        // ---- dashboard snapshot for the live E-Sports Dashboard ----
        app.MapGet("/api/state", () =>
        {
            var now = DateTimeOffset.UtcNow;
            var master = manifests.LoadMaster();
            var policy = manifests.LoadPolicy();

            List<LocalManifest> LoadReports()
            {
                var list = new List<LocalManifest>();
                foreach (var f in Directory.GetFiles(manifests.ReportsDir, "*.json"))
                    if (Wire.FromJson<LocalManifest>(File.ReadAllText(f)) is { } lm) list.Add(lm);
                return list;
            }
            var issuesByClient = LoadReports()
                .ToDictionary(r => r.ClientId,
                    r => Comparator.Games(r, master).Concat(Comparator.Software(r, policy)).ToList());

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
