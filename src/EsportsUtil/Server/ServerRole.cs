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
        var venueLogo = new VenueLogo(cfg.DataDir);
        // T35: per-operator accounts gate the dashboard. Empty store = today's open server.
        var ops = new OperatorsStore(cfg.DataDir);
        ops.BootstrapFromConfig(cfg);
        var sessions = new SessionStore();
        var loginGuard = new LoginGuard();
        var dismissals = new DismissalsStore(cfg.DataDir);   // T36: cleared compliance findings

        builder.Services.AddHostedService(_ => new LivenessMonitor(registry, hub, cfg.HeartbeatTimeoutSec));
        builder.Services.AddHostedService(sp => new DiscoveryResponder(cfg,
            (logs ?? sp.GetRequiredService<ILoggerFactory>()).CreateLogger("esportsutil.discovery")));

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

        // ---- T35: sign-in gate. Engages ONLY once an operator account exists, so the live open
        //      server changes not one byte until someone adds the first account. Agent traffic
        //      (register/heartbeat/report/job_update/factory-state/content) is NEVER gated —
        //      the fleet authenticates by client_id + operator pairing, not by web session. ----
        static bool OpenPath(string p) =>
            p is "/login" or "/api/login" or "/api/logout" or "/api/auth-status"
              or "/api/register" or "/api/heartbeat" or "/api/report" or "/api/job_update" or "/api/factory-state"
            || p.StartsWith("/content/", StringComparison.Ordinal);
        app.Use(async (ctx, next) =>
        {
            if (!ops.Any()) { await next(); return; }
            var p = ctx.Request.Path.Value ?? "/";
            if (OpenPath(p)) { await next(); return; }
            var tok = ctx.Request.Cookies[SessionStore.CookieName];
            if (!string.IsNullOrEmpty(tok) && sessions.Touch(tok!)) { await next(); return; }
            if (p.StartsWith("/api/", StringComparison.Ordinal))
                await Results.Json(new AckEnvelope(false, "sign-in required"), Wire.Options, statusCode: 401).ExecuteAsync(ctx);
            else
                ctx.Response.Redirect("/login");   // 302 the page to the sign-in card
        });

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
            return Results.Json(new AckEnvelope(true, DesiredState: e.DesiredState, DownedContentId: e.DownedContentId,
                VenueWallpaperSha256: content.Find(ContentLibrary.ContentIds.VenueWallpaper)?.Sha256), Wire.Options);
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
                DownedReason: e.DownedReason,
                VenueWallpaperSha256: content.Find(ContentLibrary.ContentIds.VenueWallpaper)?.Sha256), Wire.Options);
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
            if (report.MachineState is JsonObject ms)
            {
                var wu = ms["windowsUpdate"] as JsonObject;   // null = never polled — clears stale values
                var wuTr = registry.SetUpdateState(report.ClientId,
                    wu?["status"]?.GetValue<string>(),
                    wu?["pending_count"]?.GetValue<int?>(),
                    wu?["last_scan_unix"]?.GetValue<long?>(),
                    wu?["install_state"]?.GetValue<string>(),
                    wu?["install_done"]?.GetValue<int?>(),
                    wu?["install_total"]?.GetValue<int?>(),
                    wu?["install_error"]?.GetValue<string>(),
                    wu?["reboot_pending"]?.GetValue<bool>() ?? false);
                if (wuTr is not null)   // T33: an install run just ended (or died) — alert + restore auto-down
                {
                    var dev = registry.Snapshot().FirstOrDefault(x => x.ClientId == report.ClientId);
                    var who = string.IsNullOrWhiteSpace(dev?.DisplayName) ? $"PIN {dev?.DevicePin}" : dev!.DisplayName;
                    registry.FinishAutoDownAfterInstall(report.ClientId);
                    _ = hub.PublishAsync(new Alert(
                        wuTr == "done" ? "update_install_done" : "update_install_failed",
                        wuTr == "done" ? "info" : "warn",
                        wuTr switch
                        {
                            "done" => $"{who}: {dev?.WuInstallTotal} update(s) installed" +
                                     (dev?.WuRebootPending == true ? " · rebooting (required by Windows Update)" : " · back in service"),
                            "failed" => $"{who}: install failed after {dev?.WuInstallDone}/{dev?.WuInstallTotal}" +
                                       (dev?.WuInstallError is { Length: > 0 } err ? $" — {err}" : ""),
                            _ => $"{who}: update install interrupted (agent lost mid-install) — station returned to service",
                        },
                        report.ClientId, null, DateTimeOffset.UtcNow));
                }
                var ws = ms["wallpaperSync"] as JsonObject;   // null = nothing enforced — clears stale drift
                registry.SetWallpaperState(report.ClientId,
                    ws?["drift"]?.GetValue<bool>() ?? false,
                    ws?["expected_sha256"]?.GetValue<string>(),
                    ws?["actual_sha256"]?.GetValue<string>());
                var apps = ms["apps"] as JsonArray;           // T29 venue-app verdicts (verbatim)
                registry.SetApps(report.ClientId, apps?.ToJsonString());
                // T32: full four-view registry inventory (user: "report which apps are installed").
                registry.SetInstalledApps(report.ClientId, (ms["installedApps"] as JsonArray)?.ToJsonString());
                registry.SetSelfUpdate(report.ClientId, (ms["selfUpdate"] as JsonObject)?.ToJsonString());
            }
            return Results.Json(new AckEnvelope(true), Wire.Options);
        });

        // T32: installed_apps projection keeps operator-facing fields only — publisher/version/hive/view.
        // Uninstall strings (the agent's local command lines) deliberately never ship to a browser.
        static JsonArray CompactApps(string json)
        {
            var kept = new JsonArray();
            if (JsonNode.Parse(json) is not JsonArray arr) return kept;
            static string? G(JsonObject o, string k) => o[k] is JsonValue v ? v.ToString() : null;
            foreach (var n in arr.OfType<JsonObject>())
            {
                var name = G(n, "display_name");
                if (string.IsNullOrWhiteSpace(name)) continue;
                kept.Add(new JsonObject
                {
                    ["name"] = name, ["publisher"] = G(n, "publisher"), ["version"] = G(n, "display_version"),
                    ["hive"] = G(n, "hive"), ["view"] = G(n, "view"),
                });
            }
            var sorted = kept.Select(x => x!).OrderBy(x => x["name"]!.GetValue<string>(), StringComparer.OrdinalIgnoreCase).ToList();
            kept.Clear();
            foreach (var x in sorted) kept.Add(x);
            return kept;
        }

        // ---- factory state pull ----
        app.MapGet("/api/factory-state", () => Results.Json(manifests.LoadMaster().FactoryStateMap, Wire.Options));

        // T36: operator pulls an enrolled machine's factory state — its saved name plus every
        // installed app NAME from the last full report. Versions are deliberately NOT captured:
        // keeping apps current is the updates manager's job. Re-pull replaces the entry.
        app.MapPost("/api/factory-state/{clientId}", (string clientId) =>
        {
            var entry = registry.Snapshot().FirstOrDefault(e => e.ClientId == clientId);
            if (entry is null)
                return Results.Json(new AckEnvelope(false, "unknown client"), Wire.Options, statusCode: 404);
            var lm = manifests.LoadReport(clientId);
            if (lm is null)
                return Results.Json(new AckEnvelope(false, "that device has not sent a full report yet — wait for its first scan"),
                    Wire.Options, statusCode: 404);

            var apps = new List<string>();
            if (lm.MachineState?["installedApps"] is JsonArray arr)
                foreach (var a in arr)
                    if (a?["display_name"]?.GetValue<string>() is { Length: > 0 } dn && !apps.Contains(dn))
                        apps.Add(dn);

            var fe = new FactoryEntry(clientId, clientId,
                lm.MachineState?["machineModel"]?.GetValue<string>() ?? "unknown",
                DateTimeOffset.UtcNow, new List<GameBaseline>(), lm.MachineState ?? new JsonObject(),
                null, entry.DisplayName, apps);

            var m0 = manifests.LoadMaster();
            var map = new List<FactoryEntry>(m0.FactoryStateMap ?? new List<FactoryEntry>());
            map.RemoveAll(x => x.EntryId == clientId);   // pull again = replace, never duplicate
            map.Add(fe);
            manifests.SaveMaster(m0 with { Version = m0.Version + 1, FactoryStateMap = map });
            _ = hub.PublishAsync(new Alert("factory_state_saved", "info",
                $"factory baseline saved for #{entry.DevicePin}: {apps.Count} installed app name(s)",
                clientId, entry.DevicePin, DateTimeOffset.UtcNow));
            return Results.Json(new { ok = true, entry_id = clientId, installed_apps = apps.Count }, Wire.Options);
        });

        // T36: "clear" silences THIS station's current findings by (kind, subject) key — chips,
        // pill and the compliance alert go quiet. "restore" lifts every dismissal; if the condition
        // is still true the comparator immediately speaks again. New findings never auto-clear.
        app.MapPost("/api/issues/clear", (IssueActionRequest req) =>
        {
            if (DeviceState(req.ClientId) is not { } st)
                return Results.Json(new AckEnvelope(false, "no report from that device"), Wire.Options, statusCode: 404);
            dismissals.DismissAll(req.ClientId, st.issues, DateTimeOffset.UtcNow);
            var pin = registry.Snapshot().FirstOrDefault(e => e.ClientId == req.ClientId)?.DevicePin;
            _ = hub.PublishAsync(new Alert("compliance_cleared", "info",
                $"{st.issues.Count} compliance finding(s) dismissed by operator",
                req.ClientId, pin, DateTimeOffset.UtcNow));
            return Results.Json(new { ok = true, cleared = st.issues.Count }, Wire.Options);
        });

        app.MapPost("/api/issues/restore", (IssueActionRequest req) =>
        {
            var n = dismissals.Restore(req.ClientId);
            var pin = registry.Snapshot().FirstOrDefault(e => e.ClientId == req.ClientId)?.DevicePin;
            _ = hub.PublishAsync(new Alert("compliance_restored", "info",
                $"{n} dismissal(s) lifted — findings return if still true",
                req.ClientId, pin, DateTimeOffset.UtcNow));
            return Results.Json(new { ok = true, restored = n }, Wire.Options);
        });

        // ---- T35: operator sign-in + account management ----
        app.MapPost("/api/login", (LoginRequest req, HttpContext ctx, HttpResponse res) =>
        {
            var key = (ctx.Connection.RemoteIpAddress?.ToString() ?? "?") + "|" + (req.Username ?? "").Trim().ToLowerInvariant();
            if (loginGuard.Locked(key))
                return Results.Json(new AckEnvelope(false, "too many failed sign-ins — wait a minute"), Wire.Options, statusCode: 429);
            var user = ops.Verify(req.Username, req.Password);
            if (user is null)
            {
                loginGuard.Fail(key);
                return Results.Json(new AckEnvelope(false, "wrong username or password"), Wire.Options, statusCode: 401);
            }
            loginGuard.Pass(key);
            var token = sessions.NewToken();
            sessions.Add(token, user);
            res.Cookies.Append(SessionStore.CookieName, token, new CookieOptions
            { HttpOnly = true, SameSite = SameSiteMode.Lax, Path = "/", MaxAge = SessionStore.Ttl });
            return Results.Json(new { ok = true, username = user }, Wire.Options);
        });
        app.MapPost("/api/logout", (HttpContext ctx, HttpResponse res) =>
        {
            var tok = ctx.Request.Cookies[SessionStore.CookieName];
            if (!string.IsNullOrEmpty(tok)) sessions.Revoke(tok!);
            res.Cookies.Delete(SessionStore.CookieName, new CookieOptions { Path = "/" });
            return Results.Json(new AckEnvelope(true), Wire.Options);
        });
        app.MapGet("/api/auth-status", (HttpContext ctx) =>
        {
            var tok = ctx.Request.Cookies[SessionStore.CookieName];
            var user = string.IsNullOrEmpty(tok) ? null : sessions.User(tok!);
            return Results.Json(new { auth_required = ops.Any(), signed_in = user is not null, username = user }, Wire.Options);
        });
        app.MapGet("/api/operators", () => Results.Json(new { ok = true, operators = ops.Usernames() }, Wire.Options));
        app.MapPost("/api/operators", (OperatorAddRequest req) =>
        {
            var wasOpen = !ops.Any();
            if (!ops.Add(req.Username, req.Password, out var e))
                return Results.Json(new AckEnvelope(false, e), Wire.Options, statusCode: 400);
            if (wasOpen)   // the FIRST account is the moment the dashboard stops being open LAN-wide
                _ = hub.PublishAsync(new Alert("auth_enabled", "info",
                    "dashboard now requires operator sign-in", null, null, DateTimeOffset.UtcNow));
            return Results.Json(new AckEnvelope(true), Wire.Options);
        });
        app.MapPost("/api/operators/password", (OperatorPasswordRequest req) =>
        {
            if (!ops.SetPassword(req.Username, req.Password, out var e))
                return Results.Json(new AckEnvelope(false, e), Wire.Options, statusCode: 400);
            sessions.RevokeUser(req.Username.Trim().ToLowerInvariant());   // old sessions die with the old password
            return Results.Json(new AckEnvelope(true), Wire.Options);
        });
        app.MapDelete("/api/operators/{username}", (string username) =>
        {
            var user = username.Trim().ToLowerInvariant();
            if (!ops.Remove(username))
                return Results.Json(new AckEnvelope(false, "unknown operator"), Wire.Options, statusCode: 404);
            sessions.RevokeUser(user);
            _ = hub.PublishAsync(new Alert("operator_removed", "warn", $"operator '{user}' removed", null, null, DateTimeOffset.UtcNow));
            return Results.Json(new AckEnvelope(true), Wire.Options);
        });

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

        // Operator-requested Windows Update poll: one-shot scan_updates directive (TTL 5 min); the
        // agent polls WUA on the next beat and the fresh summary rides its next report.
        app.MapPost("/api/scan-updates/{clientId}", (string clientId) =>
            registry.QueueDirective(clientId, new Directive("scan_updates", null,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 300))
                ? Results.Json(new AckEnvelope(true), Wire.Options)
                : Results.Json(new AckEnvelope(false, "unknown device"), Wire.Options, statusCode: 404));

        // T33: the dashboard "Updates" button, for real (user: "Updates button doesn't do anything.
        // Lets have it run all updates for that unit."). One-shot install_updates directive (TTL 5 min,
        // like its siblings) PLUS an auto-down so the seat leaves service while updates install —
        // only if the station is currently active; an operator-owned down is never touched. The
        // terminal install transition in the report ingest restores active and alerts.
        app.MapPost("/api/install-updates/{clientId}", (string clientId) =>
        {
            if (!registry.QueueDirective(clientId, new Directive("install_updates", null,
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 300)))
                return Results.Json(new AckEnvelope(false, "unknown device"), Wire.Options, statusCode: 404);
            var auto = registry.AutoDownForUpdateInstall(clientId, "Windows Update install running");
            var e = registry.Snapshot().FirstOrDefault(x => x.ClientId == clientId);
            var who = string.IsNullOrWhiteSpace(e?.DisplayName) ? $"PIN {e?.DevicePin}" : e!.DisplayName;
            _ = hub.PublishAsync(new Alert("update_install_requested", "info",
                $"{who}: full Windows Update install requested — " +
                (auto ? "station held down while updates install" : "station already downed by operator"),
                clientId, null, DateTimeOffset.UtcNow));
            return Results.Json(new AckEnvelope(true), Wire.Options);
        });

        // Operator-requested self-update check+apply (T30): one-shot self_update directive (TTL 5 min).
        app.MapPost("/api/self-update/{clientId}", (string clientId) =>
            registry.QueueDirective(clientId, new Directive("self_update", null,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 300))
                ? Results.Json(new AckEnvelope(true), Wire.Options)
                : Results.Json(new AckEnvelope(false, "unknown device"), Wire.Options, statusCode: 404));

        // Operator-requested wallpaper re-sync: one-shot sync_wallpaper directive (TTL 5 min). The
        // agent re-verifies its content cache against the server manifest (re-downloading on sha
        // mismatch — heals even a corrupted cache) and re-applies the enforced image to the desktop.
        app.MapPost("/api/sync-wallpaper/{clientId}", (string clientId) =>
            registry.QueueDirective(clientId, new Directive("sync_wallpaper", null,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 300))
                ? Results.Json(new AckEnvelope(true), Wire.Options)
                : Results.Json(new AckEnvelope(false, "unknown device"), Wire.Options, statusCode: 404));

        // T29: per-venue-app install/update. The agent direct-downloads the vendor installer from
        // AppCatalog and streams progress onto the job; re-running a vendor "always latest"
        // installer IS the update path, so install_app and update_app share one handler and differ
        // only in the pre-flight guard below.
        app.MapGet("/api/app-catalog", () => Results.Json(AppCatalog.All, Wire.Options));

        Microsoft.AspNetCore.Http.IResult QueueAppJob(string clientId, string? appId, string kind)
        {
            var entry = AppCatalog.Entry(appId);
            if (entry is null)
                return Results.Json(new AckEnvelope(false, $"app '{appId}' not in venue catalog"), Wire.Options, statusCode: 400);
            if (!registry.Exists(clientId))
                return Results.Json(new AckEnvelope(false, "unknown device"), Wire.Options, statusCode: 404);
            var dev = registry.Snapshot().FirstOrDefault(x => x.ClientId == clientId);
            if (kind == "update_app")
            {
                if (dev?.AppsJson is null)
                    return Results.Json(new AckEnvelope(false, "device has never reported app state — install first"), Wire.Options, statusCode: 400);
                var row = (JsonNode.Parse(dev.AppsJson) as JsonArray)?
                    .FirstOrDefault(a => a?["app_id"]?.GetValue<string>() == entry.Id);
                if (row?["installed"]?.GetValue<bool>() != true)
                    return Results.Json(new AckEnvelope(false, $"{entry.Name} is not installed on this device — use install"), Wire.Options, statusCode: 400);
            }
            if (jobs.HasOpen(clientId, kind, entry.Id))
                return Results.Json(new AckEnvelope(false, $"a {kind} job for {entry.Name} is already queued or running"), Wire.Options, statusCode: 409);

            var job = jobs.Enqueue(clientId, kind, entry.Id, null, null);
            _ = hub.PublishAsync(new Alert("job_queued", "info", $"{kind} {entry.Name}",
                clientId, dev?.DevicePin, DateTimeOffset.UtcNow));
            // AckEnvelope shape (not a bare Job): the Settings page and dashboard chips branch on `ok`.
            return Results.Json(new AckEnvelope(true, Job: job), Wire.Options);
        }
        app.MapPost("/api/install-app/{clientId}", (string clientId, AppInstallRequest req) => QueueAppJob(clientId, req.AppId, "install_app"));
        app.MapPost("/api/update-app/{clientId}", (string clientId, AppInstallRequest req) => QueueAppJob(clientId, req.AppId, "update_app"));

        app.MapPost("/api/print-label/{clientId}", (string clientId) =>
        {
            var e = registry.Snapshot().FirstOrDefault(x => x.ClientId == clientId);
            if (e is null) return Results.Json(new AckEnvelope(false, "unknown device"), Wire.Options, statusCode: 404);
            if (e.Pending) return Results.Json(new AckEnvelope(false, "name this device (and set its location) before printing its label"), Wire.Options, statusCode: 400);
            string? logoUri = null;   // embed the venue logo so the label artifact prints standalone
            var lpath = venueLogo.FindPath();
            if (lpath is not null)
            {
                var lb = File.ReadAllBytes(lpath);
                var lk = ImageFormats.Sniff(lb);
                if (lk is not null) logoUri = $"data:{lk.Value.Mime};base64,{Convert.ToBase64String(lb)}";
            }
            var path = printer.Print(e.DisplayName, e.Location, logoUri);
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

        // ---- venue settings: logo (settings page posts raw bytes; magic-bytes gate + size cap, never trust name/MIME) ----
        app.MapGet("/api/settings", () => Results.Json(new
        {
            logo = venueLogo.Info(), max_bytes = ImageFormats.MaxLogoBytes,
            wallpaper = WallpaperInfo(), max_wallpaper_bytes = ImageFormats.MaxWallpaperBytes,
        }, Wire.Options));

        object? WallpaperInfo()
        {
            var it = content.Find(ContentLibrary.ContentIds.VenueWallpaper);
            var p = it is null ? null : content.FilePathFor(it.ContentId);
            if (p is null) return null;
            var fi = new FileInfo(p);
            return new LogoInfo(it!.Filename.Contains('.') ? FilenameExt(it.Filename) : it.Filename,
                fi.Length, new DateTimeOffset(fi.LastWriteTimeUtc, TimeSpan.Zero).ToUnixTimeSeconds());
        }
        static string FilenameExt(string f) => f[(f.LastIndexOf('.') + 1)..].ToLowerInvariant();

        // ---- venue wallpaper: the upload IS the venue-wide desired state — stored as Content Library
        //      item `venue-wallpaper`; agents converge on the sha the heartbeat ack carries ----
        app.MapPost("/settings/wallpaper", async (HttpRequest req) =>
        {
            if (req.ContentLength is > ImageFormats.MaxWallpaperBytes)
                return Results.Json(new AckEnvelope(false, "wallpaper exceeds the 8 MB cap"), Wire.Options, statusCode: 413);
            using var ms = new MemoryStream();
            var buf = new byte[81920];
            long total = 0; int n;
            while ((n = await req.Body.ReadAsync(buf)) > 0)
            {
                ms.Write(buf, 0, n); total += n;
                if (total > ImageFormats.MaxWallpaperBytes)
                    return Results.Json(new AckEnvelope(false, "wallpaper exceeds the 8 MB cap"), Wire.Options, statusCode: 413);
            }
            var bytes = ms.ToArray();
            var known = ImageFormats.Sniff(bytes);
            if (known is null)
                return Results.Json(new AckEnvelope(false, "not a supported image — header must be PNG, JPEG, BMP, GIF or WEBP"), Wire.Options, statusCode: 415);
            content.Upsert(ContentLibrary.ContentIds.VenueWallpaper, "venue-wallpaper." + known.Value.Ext, "wallpaper", bytes);
            return Results.Json(new { ok = true, wallpaper = WallpaperInfo() }, Wire.Options);
        });
        app.MapGet("/settings/wallpaper", (HttpResponse res) =>
        {
            var p = content.FilePathFor(ContentLibrary.ContentIds.VenueWallpaper);
            if (p is null) return Results.NotFound();
            var head = new byte[16];
            using (var fs = File.OpenRead(p)) fs.ReadExactly(head.AsSpan(0, (int)Math.Min(16, fs.Length)));
            res.Headers["Cache-Control"] = "no-store";
            return Results.File(p, ImageFormats.Sniff(head)?.Mime ?? "application/octet-stream");
        });
        app.MapDelete("/settings/wallpaper", () =>
            content.Remove(ContentLibrary.ContentIds.VenueWallpaper) ? Results.Json(new AckEnvelope(true), Wire.Options)
                : Results.Json(new AckEnvelope(false, "no wallpaper set"), Wire.Options, statusCode: 404));
        app.MapPost("/settings/logo", async (HttpRequest req) =>
        {
            if (req.ContentLength is > ImageFormats.MaxLogoBytes)
                return Results.Json(new AckEnvelope(false, "logo exceeds the 4 MB cap"), Wire.Options, statusCode: 413);
            using var ms = new MemoryStream();
            var buf = new byte[81920];
            long total = 0; int n;
            while ((n = await req.Body.ReadAsync(buf)) > 0)
            {
                ms.Write(buf, 0, n); total += n;
                if (total > ImageFormats.MaxLogoBytes)
                    return Results.Json(new AckEnvelope(false, "logo exceeds the 4 MB cap"), Wire.Options, statusCode: 413);
            }
            var bytes = ms.ToArray();
            var known = ImageFormats.Sniff(bytes);
            if (known is null)
                return Results.Json(new AckEnvelope(false, "not a supported image — header must be PNG, JPEG, BMP, GIF or WEBP"), Wire.Options, statusCode: 415);
            return Results.Json(new { ok = true, logo = venueLogo.Save(bytes, known.Value.Ext) }, Wire.Options);
        });
        app.MapGet("/settings/logo", (HttpResponse res) =>
        {
            var p = venueLogo.FindPath();
            if (p is null) return Results.NotFound();
            var head = new byte[16];
            using (var fs = File.OpenRead(p)) fs.ReadExactly(head.AsSpan(0, (int)Math.Min(16, fs.Length)));
            res.Headers["Cache-Control"] = "no-store";   // logo swaps must show immediately
            return Results.File(p, ImageFormats.Sniff(head)?.Mime ?? "application/octet-stream");
        });
        app.MapDelete("/settings/logo", () =>
            venueLogo.Delete() ? Results.Json(new AckEnvelope(true), Wire.Options)
                               : Results.Json(new AckEnvelope(false, "no logo set"), Wire.Options, statusCode: 404));

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
            var j = jobs.Update(req.JobId, req.ClientId, req.State, req.Detail, req.Progress);
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
                var found = issuesByClient.TryGetValue(e.ClientId, out var iss) ? iss : new List<Issue>();
                // T36: operator dismissals silence findings; the count of cleared ones rides along
                // so the row can show "N cleared ↺" and offer restore.
                var issues = found.Where(i => !dismissals.IsDismissed(e.ClientId, i.Kind, i.Subject)).ToList();
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
                    wu_status = e.WuStatus,
                    wu_pending = e.WuPending,
                    wu_scan_min_ago = e.WuScanUnix is null ? (double?)null : Math.Round((now.ToUnixTimeSeconds() - e.WuScanUnix.Value) / 60.0, 1),
                    wu_install_state = e.WuInstallState,
                    wu_install_done = e.WuInstallDone,
                    wu_install_total = e.WuInstallTotal,
                    wu_reboot_pending = e.WuRebootPending ? true : (bool?)null,
                    wallpaper_drift = e.WpDrift,
                    apps = e.AppsJson is null ? null : JsonNode.Parse(e.AppsJson),
                    installed_apps = e.InstalledAppsJson is null ? null : CompactApps(e.InstalledAppsJson),
                    self_update = e.SelfUpdateJson is null ? null : JsonNode.Parse(e.SelfUpdateJson),
                    games = sum?.GameCount,
                    duplicates = sum?.DuplicateCount,
                    agent_version = sum?.AgentVersion,
                    last_report = sum?.ReportTimestamp,
                    locate_pending = e.PendingDirectives.Any(d => d.ExpiresUnix > now.ToUnixTimeSeconds()),
                    issues,
                    cleared_issues = found.Count - issues.Count,
                    jobs = jobs.ForClient(e.ClientId),
                };
            }).ToList();

            // Compliance alerting: publish once per device whenever its issue set changes.
            // T36: dismissed findings are NOT part of the issue set — clearing quiets the alert too.
            foreach (var (cid, found) in issuesByClient)
            {
                var issues = found.Where(i => !dismissals.IsDismissed(cid, i.Kind, i.Subject)).ToList();
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
                server_version = SelfUpdateWatch.CurrentVersion,
                devices,
                alerts = hub.Recent().AsEnumerable().Reverse(),
                budgets = cfg.Budgets,
            }, Wire.Options);
        });

        // ---- dashboard HTML (served by the EXE, rendered in Edge) ----
        var loginPath = Path.Combine(AppContext.BaseDirectory, "wwwroot", "login.html");
        app.MapGet("/login", () => File.Exists(loginPath)
            ? Results.Content(File.ReadAllText(loginPath), "text/html")
            : Results.Text("login.html missing next to esportsutil"));
        var dashPath = Path.Combine(AppContext.BaseDirectory, "wwwroot", "dashboard.html");
        app.MapGet("/", () => File.Exists(dashPath)
            ? Results.Content(File.ReadAllText(dashPath), "text/html")
            : Results.Text("dashboard.html missing next to esportsutil"));
        var settingsPath = Path.Combine(AppContext.BaseDirectory, "wwwroot", "settings.html");
        app.MapGet("/settings", () => File.Exists(settingsPath)
            ? Results.Content(File.ReadAllText(settingsPath), "text/html")
            : Results.Text("settings.html missing next to esportsutil"));

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
