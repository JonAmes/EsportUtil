using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using EsportsUtil.Agent;
using EsportsUtil.Core;
using EsportsUtil.Server;

// Dependency-free test runner (no NuGet needed; AGENTS.md: continuous verification per phase).
// Exit code = number of failed checks. Nightly full-suite note: the perf gate here samples a
// shortened window; the 5-minute averaged gate from PLAN.md runs the same code with PERF_SECS=300.

int failed = 0;
void Check(bool ok, string name)
{
    Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name);
    if (!ok) failed++;
}
async Task<bool> Until(Func<Task<bool>> f, int secs = 10)
{
    var deadline = DateTime.UtcNow.AddSeconds(secs);
    while (DateTime.UtcNow < deadline) { if (await f()) return true; await Task.Delay(250); }
    return false;
}

var tmp = Path.Combine(Path.GetTempPath(), "esu-tests-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(tmp);

// ---------- T1: scanner primitives ----------
Check(GameScanner.NormalizeFolder("Counter-Strike 2!") == "counter_strike_2", "T1a folder normalization");
Check(GameScanner.DetectLauncher(@"C:\Program Files (x86)\Steam") == "steam", "T1b steam root detection");
var g1 = Path.Combine(tmp, "root1", "Dota 2");
Directory.CreateDirectory(g1); File.WriteAllText(Path.Combine(g1, "version.txt"), "7.10\n");
Check(GameScanner.ProbeVersion(Path.Combine(tmp, "root1", "Dota 2")) == "7.10", "T1c version probe");
Check(GameScanner.ProbeVersion(tmp) == "unknown", "T1d unknown version fallback");

// ---------- T2: duplicate detection across roots ----------
var rootA = Path.Combine(tmp, "root1");
var rootB = Path.Combine(tmp, "root2");
Directory.CreateDirectory(Path.Combine(rootB, "counter_strike_2"));
Directory.CreateDirectory(Path.Combine(rootA, "Counter-Strike 2"));
var (reports, dupes) = GameScanner.Scan(new[] { rootA, rootB });
Check(dupes.Count == 1 && dupes[0].Paths.Count == 2, "T2a duplicate reported across roots");
Check(reports.Count(r => r.Status == "DUPLICATE_FOUND") == 2, "T2b both copies flagged DUPLICATE_FOUND");

// ---------- T3/T4/T5: registry onboarding, rename, downed, directives ----------
var reg = new DeviceRegistry(Path.Combine(tmp, "reg"));
Check(!reg.TryRegister("c1", "12", null, out _), "T3a bad PIN rejected (not 4 digits)");
Check(reg.TryRegister("c1", "0042", "Precision 7920 Tower", out var e1) && e1.Pending, "T3b register -> pending device");
Check(!reg.Rename("c1", "  "), "T3c blank rename rejected");
Check(reg.Rename("c1", "LAN-PC-01"), "T3d rename accepted");
Check(!reg.Rename("c2", "Pending"), "T3e reserved name 'pending' rejected for unnamed device");
Check(reg.Rename("c1", "LAN-PC-01A"), "T3f rename is updateable");
Check(reg.Rename("c1", "LAN-PC-01A", "Zone B · Row 3"), "T3g rename can stamp location (onboarding modal)");
Check(reg.Snapshot().First(x => x.ClientId == "c1").Location == "Zone B · Row 3", "T3h location persisted on registry entry");
Check(reg.Rename("c1", "LAN-PC-01A", ""), "T3i blank location clears without rejecting the rename");
Check(reg.Snapshot().First(x => x.ClientId == "c1").Location is null, "T3j cleared location round-trips as null");

Check(reg.TryRegister("c2", "0043", null, out var e2), "T4a second device registered");
Check(reg.SetDowned("c2", "downed", "out-of-service", "maintenance"), "T4b downed settable before any heartbeat (queued)");
Check(e2.DesiredState == "downed" && e2.DownedContentId == "out-of-service", "T4c desired_state persisted for offline agent");
Check(!reg.SetDowned("c2", "locked", null, null), "T4d invalid desired_state rejected");

reg.QueueDirective("c1", new Directive("locate", null, DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 1));
reg.QueueDirective("c1", new Directive("locate", null, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 5)); // already expired
Check(reg.TakeDirectives("c1").Count == 1, "T5a expired directive dropped, live one delivered");
Check(reg.TakeDirectives("c1").Count == 0, "T5b directives are one-shot (cleared after ack)");

// ---------- T6: wire format is snake_case per schemas ----------
var m = new LocalManifest("c1", "0042", null, DateTimeOffset.UnixEpoch, "0.1.0",
    StateModule.Record(), new List<GameReport> { new("g", "G", "/g", "1", "steam", "OK") });
var json = Wire.ToJson(m);
Check(json.Contains("\"client_id\"") && json.Contains("\"game_reports\"") && json.Contains("\"game_id\"")
      && json.Contains("\"device_pin\""), "T6a snake_case wire keys per schemas");
var back = Wire.FromJson<LocalManifest>(json);
Check(back is not null && back.GameReports[0].GameId == "g" && back.DevicePin == "0042", "T6b round-trip");

// ---------- T7: identity/PIN derivation ----------
var pin1 = Identity.DerivePin("seed-A"); var pin2 = Identity.DerivePin("seed-A"); var pin3 = Identity.DerivePin("seed-B");
Check(Regex.IsMatch(pin1, "^[0-9]{4}$") && pin1 == pin2 && pin3 != pin1, "T7 PIN derivation deterministic, 4-digit, seed-sensitive");

// ---------- T8: full E2E — real server + real agent over HTTP ----------
var port = 4399;
var srvUrl = $"http://127.0.0.1:{port}";
var srvCfg = new EsportsConfig { Role = "server", ServerUrl = srvUrl, DataDir = Path.Combine(tmp, "srv") };
srvCfg.HeartbeatTimeoutSec = 4;
using var app = ServerRole.BuildApp(srvCfg);
await app.StartAsync();
using var probe = new HttpClient { BaseAddress = new Uri(srvUrl), Timeout = TimeSpan.FromSeconds(5) };
async Task<JsonDoc> State() => Wire.FromJson<JsonDoc>(await probe.GetStringAsync("/api/state"))!;
async Task<bool> PostAck(string path, object body) =>
    (Wire.FromJson<AckEnvelope>(await (await probe.PostAsync(path,
        new StringContent(Wire.ToJson(body), System.Text.Encoding.UTF8, "application/json")))
        .Content.ReadAsStringAsync()))?.Ok == true;

var agentDir = Path.Combine(tmp, "agent");
var agCfg = new EsportsConfig { Role = "agent", ServerUrl = srvUrl, DataDir = agentDir };
agCfg.HeartbeatIntervalSec = 1; agCfg.SlowScanIntervalMin = 0;
agCfg.ScanDirs = new List<string> { rootA, rootB };
using var cts = new CancellationTokenSource();
var loop = new AgentLoop(new HttpClient { Timeout = TimeSpan.FromSeconds(5) }, agCfg);
var overlayReqs = new List<LocateOverlayRequest>();          // probe instead of spawning real overlay children
loop.OverlaySurface = r => overlayReqs.Add(r);
var downReqs = new List<DownedOverlayRequest>();             // sticky downed overlay probe (T21)
var downHandles = new List<DownProbe>();
loop.DownOverlaySurface = r => { downReqs.Add(r); var h = new DownProbe(); downHandles.Add(h); return h; };
var loopTask = loop.RunAsync(cts.Token);

Check(await Until(async () => (await State()).devices.Any(d => d.games >= 2 && d.duplicates == 1)),
    "T8a agent registered + manifest reported (with duplicate flagged)");

var cid = (await State()).devices[0].client_id;
Check(await PostAck("/api/rename", new RenameRequest(cid, "LAN-PC-TEST", "Zone A · Stage 2")), "T8b operator rename via API (with location)");
Check((await State()).devices[0].location == "Zone A · Stage 2", "T8b2 /api/state surfaces location so the dashboard shows/edits it");
Check(await PostAck("/api/downed", new DownedRequest(cid, "downed", "out-of-service", "maintenance")), "T8c downed set via API");
Check(await Until(async () => loop.LastDesiredState == "downed"), "T8d agent receives desired_state via heartbeat ack");

await probe.PostAsync("/api/locate/" + cid, new StringContent(""));
Check(await Until(async () => loop.ConsumedDirectives.Any(d => d.Type == "locate"), 6), "T8e locate directive delivered once via ack");
Check(await Until(() => Task.FromResult(overlayReqs.Count == 1
        && overlayReqs[0].ExpiresUnix > DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        && overlayReqs[0].DisplayName == "LAN-PC-TEST"), 6),
    "T8e2 locate button triggers the overlay surface exactly once, live expiry, operator display_name in payload");

// hash dedupe: identical report content -> no repeated uploads despite report timer every heartbeat
await Task.Delay(3500); // 3+ heartbeat/report cycles with unchanged content
Check(loop.ReportsSent <= 2, "T8f content-hash dedupe: no re-upload of unchanged manifest");

// ---------- T19: operator offboarding (PIN revoke + identity tombstone) ----------
{
    async Task<(int code, AckEnvelope ack)> PostRaw(string path, object body)
    {
        var r = await probe.PostAsync(path, new StringContent(Wire.ToJson(body), System.Text.Encoding.UTF8, "application/json"));
        return ((int)r.StatusCode, Wire.FromJson<AckEnvelope>(await r.Content.ReadAsStringAsync())!);
    }

    var t19 = Guid.NewGuid().ToString();
    Check(await PostAck("/api/register", new RegisterRequest(t19, "7777", "Ghost Tower")), "T19a one-shot device registers via API");
    Check((await State()).devices.Any(d => d.client_id == t19), "T19b registered device visible in /api/state");
    Check(await PostAck("/api/offboard/" + t19, new { }), "T19c operator offboards (trashcan / PIN-revoke action)");
    Check(!(await State()).devices.Any(d => d.client_id == t19), "T19d offboarded device is gone from /api/state");
    var (rc, rack) = await PostRaw("/api/register", new RegisterRequest(t19, "7777", "Ghost Tower"));
    Check(rc == 403 && rack.Error!.Contains("offboarded"), "T19e revoked client_id can never re-register (tombstone)");
    var (hc, hack) = await PostRaw("/api/heartbeat", new HeartbeatRequest(t19, "7777", DateTimeOffset.UtcNow, AgentInfo.Version));
    Check(hc == 410 && hack.Error!.Contains("offboarded"), "T19f heartbeat from a revoked identity refused outright");
    Check(await PostRaw("/api/offboard/" + Guid.NewGuid(), new { }) is { code: 404 }, "T19g offboarding an unknown device fails cleanly (404)");

    Check(reg.Offboard("c1") && !reg.Snapshot().Any(x => x.ClientId == "c1") && reg.IsRevoked("c1"),
        "T19h registry Offboard removes entry and tombstones client_id");
    Check(!reg.TryRegister("c1", "0042", null, out _), "T19i tombstoned client_id blocked at registry level too");
    Check(new DeviceRegistry(Path.Combine(tmp, "reg")).IsRevoked("c1"), "T19j revoked tombstone survives server restart (revoked.json)");

    var rotDir = Path.Combine(tmp, "rot-identity");
    var idA = Identity.LoadOrCreate(rotDir);
    var idB = Identity.Rotate(rotDir);
    Check(idB.ClientId != idA.ClientId && idB.DevicePin == idA.DevicePin,
        "T19k rotated identity: new client_id, machine PIN kept for re-pairing");

    var dashPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
        "EsportsUtil", "wwwroot", "dashboard.html"));
    var dashHtml = File.ReadAllText(dashPath);
    Check(dashHtml.Contains("/api/offboard/") && dashHtml.Contains("btn danger") && dashHtml.Contains("pinx"),
        "T19l dashboard wires trashcan AND clickable PIN pill to /api/offboard");
}

// ---------- T20: agent client dashboard (read-only localhost view served by the agent) ----------
{
    agCfg.AgentDashboardPort = 0;                                   // ephemeral loopback bind in tests
    var dash = ClientDashboard.Start(loop, agCfg);
    using var view = new HttpClient { BaseAddress = new Uri(dash.Url), Timeout = TimeSpan.FromSeconds(5) };
    async Task<LocalStatus> View() => Wire.FromJson<LocalStatus>(await view.GetStringAsync("/api/local_status"))!;

    Check(await Until(async () =>
    {
        var v = await View();
        return v.ClientId == cid && v.Registered && v.AgentVersion == AgentInfo.Version;
    }, 6), "T20a client view mirrors live agent identity + registration from the loop");

    Check((await View()).DevicePin == (await State()).devices.First(d => d.client_id == cid).device_pin,
        "T20b client view PIN matches the registry PIN the operator sees server-side");

    Check(await Until(async () => (await View()).DisplayName == "LAN-PC-TEST", 6),
        "T20c heartbeat ack display_name streams the operator pairing name into the client view");

    var page = await view.GetStringAsync("/");
    Check(page.Contains("/api/local_status") && page.Contains("Client Agent") && page.Contains("awaiting pairing"),
        "T20d agent serves client.html: polls /api/local_status with PIN/pairing sections");

    await PostAck("/api/downed", new DownedRequest(cid, "active", null, null));
    Check(await Until(async () => (await View()).DesiredState == "active", 6),
        "T20e active desired_state flows from server into the client view");
    await PostAck("/api/downed", new DownedRequest(cid, "downed", "out-of-service", "T20 drill"));
    Check(await Until(async () => (await View()).DesiredState == "downed", 6),
        "T20f downed desired_state flows into the client view state pill");
    await PostAck("/api/downed", new DownedRequest(cid, "active", null, null));
    await Until(async () => (await View()).DesiredState == "active", 6);

    Check(await Until(async () =>
    {
        var v = await View();
        return v.ReportsSent >= 1 && v.Games.Count >= 2 && v.Duplicates.Count >= 1 && !string.IsNullOrEmpty(v.ReportHash);
    }, 8), "T20g client view shows the last local manifest: detected games, duplicates, report hash");

    // a real server-issued job runs through the loop and surfaces in the client view with final state
    // (seed server-1's content library — mtime-invalidated load — then deploy that asset as a job;
    //  wallpaper apply is a no-op on Linux so final state may be either — the gate is visibility)
    var srvContent = Path.Combine(tmp, "srv", "content");
    Directory.CreateDirectory(srvContent);
    var t20png = new byte[] { 0x89, 0x50, 0x4E, 0x47 };
    File.WriteAllBytes(Path.Combine(srvContent, "t20-oos.png"), t20png);
    File.WriteAllText(Path.Combine(tmp, "srv", "ContentManifest.json"), Wire.ToJson(new ContentManifest(1, DateTimeOffset.UtcNow,
        new List<ContentItem> { new("t20-oos", "t20-oos.png", Convert.ToHexString(SHA256.HashData(t20png)).ToLowerInvariant(), "wallpaper") })));
    var jr = await probe.PostAsync("/api/jobs", new StringContent(Wire.ToJson(new NewJobRequest(cid, "deploy_content", "t20-oos")), System.Text.Encoding.UTF8, "application/json"));
    Check((int)jr.StatusCode == 200, "T20h deploy_content job accepted for the loop device");
    Check(await Until(async () => (await View()).RecentJobs.Any(j => j.Kind == "deploy_content" && j.State is "succeeded" or "failed"), 20),
        "T20i executed job surfaces in the client view Jobs section with final state");

    // ---------- T21: sticky downed overlay — no expiry, staff bypass, episode lifecycle ----------
    var t21Base = downReqs.Count;                                    // earlier T8/T20 drills already left episodes
    await PostAck("/api/downed", new DownedRequest(cid, "downed", null, "GPU riser fan loud"));
    Check(await Until(() => Task.FromResult(downReqs.Count == t21Base + 1
              && downReqs[^1].Reason == "GPU riser fan loud"), 6),
        "T21a downed toggle surfaces a sticky downed overlay carrying the operator note");
    Check(downReqs[^1].ShouldShow(DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 315_360_000L),
        "T21b overlay has no expiration — still shows ten years out");
    Check(downReqs[^1].AccentHex == "#d97706" && !downReqs[^1].HasCountdown,
        "T21c sticky variant keeps the dashboard downed amber (#d97706) and carries no countdown");
    Check(await Until(async () => (await View()).DownedReason == "GPU riser fan loud", 6),
        "T21d operator downed note streams into the client view via heartbeat ack");
    var h = downHandles[^1];
    await Task.Delay(3000);                                          // ~3 heartbeat beats
    Check(downReqs.Count == t21Base + 1 && h.Alive,
        "T21e overlay stands across heartbeats — no respawn, no auto-dismiss");
    h.AliveFlag = false;                                             // staff presses BYPASS on the machine
    await Task.Delay(2500);
    Check(downReqs.Count == t21Base + 1,
        "T21f staff bypass is honored — no re-show for the rest of the downed episode");
    await PostAck("/api/downed", new DownedRequest(cid, "active", null, null));
    Check(await Until(() => Task.FromResult(loop.LastDesiredState == "active"), 6),
        "T21g-pre agent observes the active flip (episode resets)");
    await PostAck("/api/downed", new DownedRequest(cid, "downed", null, "re-flagged after repair"));
    Check(await Until(() => Task.FromResult(downReqs.Count == t21Base + 2
              && downReqs[^1].Reason == "re-flagged after repair"), 6),
        "T21g active→downed flip starts a new episode — overlay shows again");
    await PostAck("/api/downed", new DownedRequest(cid, "active", null, null));
    Check(await Until(() => Task.FromResult(downHandles[^1].WasKilled), 6),
        "T21h operator clear kills the live overlay child");

    dash.Stop();
}

// liveness: agent stops heartbeating -> critical alert fires
cts.Cancel(); await loopTask;
Check(await Until(async () => (await State()).alerts.Any(a => a.type == "heartbeat_lost" && a.severity == "critical"), 15),
    "T8g heartbeat-lost critical alert after timeout");
await app.StopAsync();

// ---------- T9: Phase 2 comparator unit checks ----------
Check(Versions.Cmp("1.9", "1.10") < 0 && Versions.Cmp("7.10", "7.9") > 0 && Versions.Cmp("2.1.0.3", "2.1.0.3") == 0
    && Versions.Cmp("2.1.0.3", "unknown") > 0, "T9a version ordering 1.9 < 1.10; junk versions rank last");
var rep = new LocalManifest("cx", "0001", null, DateTimeOffset.UnixEpoch, "0.1.0",
    new System.Text.Json.Nodes.JsonObject
    {
        ["machineModel"] = "Model X",
        ["installedApps"] = new System.Text.Json.Nodes.JsonArray
        {
            new System.Text.Json.Nodes.JsonObject { ["display_name"] = "Steam" },
            new System.Text.Json.Nodes.JsonObject { ["display_name"] = "Microsoft Visual C++ 2019" },
            new System.Text.Json.Nodes.JsonObject { ["display_name"] = "Chrome" },
            new System.Text.Json.Nodes.JsonObject { ["display_name"] = "EsportsUtil Agent" },
        },
    },
    new List<GameReport>
    {
        new("a", "A", "/a", "1.0.1", "steam", "OK"),
        new("b", "B", "/b", "2.0", "steam", "OK"),
        new("c", "C", "/c", "1.0", "steam", "OK"),
        new("e", "E", "/e1", "1", "steam", "DUPLICATE_FOUND"),
        new("e", "E", "/e2", "2", "steam", "DUPLICATE_FOUND"),
    },
    new List<DuplicateReport> { new("e", new List<string> { "/e1", "/e2" }) });
var mm = new MasterManifest(1, DateTimeOffset.UnixEpoch,
    new List<MasterGame> { new("a", "1.0.5"), new("b", "1.0"), new("d", "3.0"), new("e", "2") }, new());
var giss = Comparator.Games(rep, mm);
Check(giss.Any(i => i.Kind == "update_required" && i.Subject == "a" && i.WantedVersion == "1.0.5"), "T9b update_required from master diff");
Check(giss.Any(i => i.Kind == "rollback_required" && i.Subject == "b"), "T9c rollback_required");
Check(giss.Any(i => i.Kind == "unmanaged_game" && i.Subject == "c"), "T9d unmanaged_game");
Check(giss.Any(i => i.Kind == "missing_from_machine" && i.Subject == "d"), "T9e missing_from_machine (master game absent)");
Check(giss.Any(i => i.Kind == "duplicate_resolved" && i.Subject == "e" && i.KeepPath == "/e2"),
    "T9f duplicate resolution keeps the master-matching copy");
var pol = new ApprovedSoftwarePolicy(1, DateTimeOffset.UnixEpoch, "report_only",
    new List<SoftwareRule> { new("Steam"), new("*", NamePrefix: "Microsoft Visual C++ 20") },
    new List<SoftwareRule> { new("EsportsUtil Agent") });
var sugg = Comparator.Software(rep, pol);
Check(sugg.Count(i => i.Kind == "unapproved_app") == 2, "T9g allow-list flags only unmatched apps (Steam + VC++ redist pass)");
Check(sugg.Any(i => i.Subject == "EsportsUtil Agent" && i.Protected), "T9h protected list flagged with removal-veto marker");
Check(Comparator.Software(rep, null).Count == 0, "T9i absent policy = evaluation disabled");
Check(Comparator.Hash(giss) == Comparator.Hash(giss.AsEnumerable().Reverse()), "T9j issue-set hash stable under reordering");

// ---------- T10: Phase 2 E2E — /api/state issues + compliance alert ----------
var port2 = 4398; var srvUrl2 = $"http://127.0.0.1:{port2}"; var srvDir2 = Path.Combine(tmp, "srv2");
Directory.CreateDirectory(srvDir2);
File.WriteAllText(Path.Combine(srvDir2, "MasterManifest.json"), Wire.ToJson(new MasterManifest(1, DateTimeOffset.UtcNow,
    new List<MasterGame> { new("dota_2", "7.11"), new("counter_strike_2", "2.1.0.3"), new("fortnite", "1.0") }, new())));
File.WriteAllText(Path.Combine(srvDir2, "ApprovedSoftwarePolicy.json"), Wire.ToJson(
    new ApprovedSoftwarePolicy(1, DateTimeOffset.UtcNow, "report_only", new List<SoftwareRule>(), new List<SoftwareRule>())));
var srvCfg2 = new EsportsConfig { Role = "server", ServerUrl = srvUrl2, DataDir = srvDir2 }; srvCfg2.HeartbeatTimeoutSec = 30;
using var app2 = ServerRole.BuildApp(srvCfg2); await app2.StartAsync();
using var probe2 = new HttpClient { BaseAddress = new Uri(srvUrl2), Timeout = TimeSpan.FromSeconds(5) };
var agCfg2 = new EsportsConfig { Role = "agent", ServerUrl = srvUrl2, DataDir = Path.Combine(tmp, "agent2") };
agCfg2.HeartbeatIntervalSec = 1; agCfg2.SlowScanIntervalMin = 0; agCfg2.ScanDirs = new List<string> { rootA, rootB };
using var cts2 = new CancellationTokenSource();
var loop2 = new AgentLoop(new HttpClient { Timeout = TimeSpan.FromSeconds(5) }, agCfg2);
var loopTask2 = loop2.RunAsync(cts2.Token);
Check(await Until(async () =>
{
    var dev = (Wire.FromJson<JsonDoc>(await probe2.GetStringAsync("/api/state"))!).devices.FirstOrDefault();
    return dev is { issues.Count: > 0 }
        && dev.issues.Any(i => i.kind == "update_required" && i.subject == "dota_2")
        && dev.issues.Any(i => i.kind == "duplicate_resolved" && i.subject == "counter_strike_2")
        && dev.issues.Any(i => i.kind == "missing_from_machine" && i.subject == "fortnite");
}, 20), "T10a /api/state carries update_required + duplicate_resolved + missing_from_machine");
Check(await Until(async () => (Wire.FromJson<JsonDoc>(await probe2.GetStringAsync("/api/state"))!).alerts
    .Any(a => a.type == "compliance" && a.severity == "warn"), 10),
    "T10b compliance warn alert fires once when the issue set appears");
cts2.Cancel(); await loopTask2; await app2.StopAsync();

// ---------- T12: Phase 3 — toast XML + notification daemon drains queue, survives notifier failure ----------
var toastXml = ToastXml.Build(new Alert("compliance", "critical", "Dota <2> & \"x\"", "c1", "8842", DateTimeOffset.UtcNow), "http://127.0.0.1:4300/");
Check(toastXml.Contains("scenario=\"immediate\"") && toastXml.Contains("Notification.IM")
    && toastXml.Contains("Dota &lt;2&gt; &amp; &quot;x&quot;") && toastXml.Contains("#8842")
    && toastXml.Contains("activationType=\"uri\""),
    "T12a critical toast: immediate scenario, escaped body, PIN line, dashboard action");
var infoXml = ToastXml.Build(new Alert("heartbeat_recovered", "info", "back", "c1", null, DateTimeOffset.UtcNow), "http://x/");
Check(!infoXml.Contains("scenario=\"immediate\"") && infoXml.Contains("duration=\"short\""),
    "T12b info toast: short duration, never immediate");

var hub12 = new AlertHub();
var delivered = new List<string>();
var notifySvc = new NotificationService(hub12, new RecordingNotifier(delivered),
    Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
await notifySvc.StartAsync(CancellationToken.None);
await hub12.PublishAsync(new Alert("a1", "info", "m", "c1", "1111", DateTimeOffset.UtcNow));
await hub12.PublishAsync(new Alert("boom", "warn", "toast host missing", "c1", "1111", DateTimeOffset.UtcNow));
await hub12.PublishAsync(new Alert("a2", "critical", "m", "c1", "1111", DateTimeOffset.UtcNow));
Check(await Until(() => Task.FromResult(delivered.Count == 3), 10), "T12c hosted NotificationService drains the queue");
Check(delivered.SequenceEqual(new[] { "a1", "boom", "a2" }),
    "T12d notifier throw does not stop the queue (order preserved, delivery continues)");
await notifySvc.StopAsync(CancellationToken.None);

// ---------- T13: Phase 4 units — job-runner guards, job store durability, wallpaper primitive ----------
var argv13 = JobRunner.SteamCmdArgs("/games/cs2", "730");
Check(argv13[0] == "+force_install_dir" && argv13[1] == "/games/cs2"
    && argv13.IndexOf("+force_install_dir") < argv13.IndexOf("+app_update") && argv13.Contains("validate") && argv13[^1] == "+quit",
    "T13a steamcmd argv is structured: force_install_dir first, validate, quit");
Check(Throws<ArgumentException>(() => JobRunner.SteamCmdArgs("/x", "730 && rm -rf /"))
    && Throws<ArgumentException>(() => JobRunner.SteamCmdArgs("/x", ""))
    && Throws<ArgumentException>(() => JobRunner.SteamCmdArgs("  ", "730")),
    "T13b app-id digits-only + install-dir required — command injection impossible");
Check(JobRunner.DuplicateCleanupPlan(new[] { "/g/cs2/a", "/g/cs2/b" }, "/g/cs2/a").SequenceEqual(new[] { "/g/cs2/b" })
    && JobRunner.DuplicateCleanupPlan(new[] { "/g/cs2/a" }, "/G/CS2/A").Count == 0,
    "T13c cleanup deletes only non-keep copies; keep match is case-insensitive");
Check(Throws<InvalidOperationException>(() => JobRunner.DuplicateCleanupPlan(new[] { "/g/cs2/a" }, "/g/cs2/gone")),
    "T13d cleanup refuses when keep-path is no longer on disk — never deletes the wrong copy");
Check(JobRunner.PickUninstall("X", "/q /silent", "/u") == "/q /silent"
    && JobRunner.PickUninstall("X", null, "/u") == "/u"
    && JobRunner.PickUninstall("X", null, null)!.StartsWith("winget"),
    "T13e uninstall precedence: quiet > interactive > winget last resort");

var jd13 = Path.Combine(tmp, "jobs13");
var js1 = new JobStore(jd13);
var j1 = js1.Enqueue("c1", "cleanup_duplicate", "e", null, "/g/a");
var j2 = js1.Enqueue("c1", "update_game", "a", "730", "/g/a");
Check(js1.NextQueued("c1")!.JobId == j1.JobId && js1.HasOpen("c1", "update_game", "a"),
    "T13f oldest queued job handed out first; HasOpen dedupes creation");
Check(js1.Update(j1.JobId, "c1", "running", null)!.State == "running"
    && js1.Update(j1.JobId, "c1", "succeeded", "done")!.State == "succeeded"
    && js1.Update(j1.JobId, "c1", "failed", null) is null,
    "T13g running→succeeded stamps transitions; terminal states are final");
var js1b = new JobStore(jd13);
Check(js1b.ForClient("c1").Any(j => j.JobId == j1.JobId && j.State == "succeeded"),
    "T13h Jobs.json survives a server restart (durable queue)");
var jz = js1.Enqueue("c2", "update_game", "a", "730", "/x");
js1.NextQueued("c2"); js1.Update(jz.JobId, "c2", "running", null);
Check(js1.FailAbandoned(0).Count == 0, "T13i running jobs inside the grace window are left alone");
Check(js1.FailAbandoned(-10000).Count == 1 && js1.Update(jz.JobId, "c2", "succeeded", null) is null,
    "T13j abandoned running jobs fail past timeout — fleet never stalls on a zombie job");

var wpDir = Path.Combine(tmp, "wp"); Directory.CreateDirectory(wpDir);
Wallpaper.Apply(Path.Combine(wpDir, "down.png"), wpDir, "out-of-service");
Check(Wallpaper.Read(wpDir) is { Current: not null, ContentId: "out-of-service" }, "T13k downed applies wallpaper and records content_id");
Wallpaper.Restore(wpDir);
Check(Wallpaper.Read(wpDir)!.ContentId is null && Wallpaper.Read(wpDir)!.Current is null,
    "T13l clear restores baseline and releases the content_id");

// ---------- T14: Phase 4 E2E — server-issued jobs executed by a real agent over HTTP ----------
var port4 = 4397; var srvUrl4 = $"http://127.0.0.1:{port4}"; var srvDir4 = Path.Combine(tmp, "srv4");
Directory.CreateDirectory(Path.Combine(srvDir4, "content"));
File.WriteAllText(Path.Combine(srvDir4, "MasterManifest.json"), Wire.ToJson(new MasterManifest(1, DateTimeOffset.UtcNow,
    new List<MasterGame> { new("dota_2", "7.11", AppId: "570"), new("counter_strike_2", "2.1.0.3"), new("fortnite", "1.0") }, new())));
var png = new byte[] { 0x89, 0x50, 0x4E, 0x47 }; var pngSha = Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant();
File.WriteAllBytes(Path.Combine(srvDir4, "content", "down-oos.png"), png);
File.WriteAllText(Path.Combine(srvDir4, "ContentManifest.json"), Wire.ToJson(new ContentManifest(1, DateTimeOffset.UtcNow,
    new List<ContentItem> { new("down-oos", "down-oos.png", pngSha, "wallpaper") })));
var srvCfg4 = new EsportsConfig { Role = "server", ServerUrl = srvUrl4, DataDir = srvDir4 };
using var app4 = ServerRole.BuildApp(srvCfg4); await app4.StartAsync();
using var probe4 = new HttpClient { BaseAddress = new Uri(srvUrl4), Timeout = TimeSpan.FromSeconds(5) };
var agentDir4 = Path.Combine(tmp, "agent4");
var agCfg4 = new EsportsConfig { Role = "agent", ServerUrl = srvUrl4, DataDir = agentDir4 };
agCfg4.HeartbeatIntervalSec = 1; agCfg4.SlowScanIntervalMin = 0; agCfg4.ScanDirs = new List<string> { rootA, rootB };
agCfg4.SteamCmdPath = Path.Combine(tmp, "no-such-steamcmd");   // deterministic failure: updater absent on dev box
using var cts4 = new CancellationTokenSource();
var loop4 = new AgentLoop(new HttpClient { Timeout = TimeSpan.FromSeconds(15) }, agCfg4);
var loopTask4 = loop4.RunAsync(cts4.Token);

string cid4 = ""; string dupKeep = "";
await Until(async () =>
{
    var dev = (await State4()).devices.FirstOrDefault();
    if (dev is null || dev.issues.Count == 0) return false;
    cid4 = dev.client_id; dupKeep = dev.issues.First(i => i.kind == "duplicate_resolved").keep_path!;
    return true;
}, 20);
async Task<JsonDoc> State4() => Wire.FromJson<JsonDoc>(await probe4.GetStringAsync("/api/state"))!;
async Task<bool> PostAck4(string path, object body) =>
    (Wire.FromJson<AckEnvelope>(await (await probe4.PostAsync(path,
        new StringContent(Wire.ToJson(body), System.Text.Encoding.UTF8, "application/json")))
        .Content.ReadAsStringAsync()))?.Ok == true;
async Task<(int code, string body)> Job(string kind, string subject)
{
    var r = await probe4.PostAsync("/api/jobs", new StringContent(Wire.ToJson(new NewJobRequest(cid4, kind, subject)), System.Text.Encoding.UTF8, "application/json"));
    return ((int)r.StatusCode, await r.Content.ReadAsStringAsync());
}
var (code4a, _) = await Job("cleanup_duplicate", "counter_strike_2");
var (code4b, _) = await Job("cleanup_duplicate", "counter_strike_2");           // duplicate request
var (code4c, _) = await Job("remove_software", "Chrome");                        // no such issue
var (code4d, _) = await Job("update_game", "fortnite");                          // master game without app_id
var (code4e, _) = await Job("update_game", "dota_2");                            // update_required issue + app_id → accepted
Check(code4a == 200 && code4b == 409 && code4c == 400 && code4d == 400 && code4e == 200,
    $"T14a job guards: create 200, duplicate 409, no-issue 400, no-app-id 400 (got {code4a}/{code4b}/{code4c}/{code4d}/{code4e})");
Check(dupKeep.Length > 0, "T14b server resolved a keep-path for the duplicate before dispatch");

Check(await Until(async () =>
{
    var jobs4 = (await State4()).devices.First().jobs ?? new();
    return jobs4.Any(j => j.kind == "cleanup_duplicate" && j.state == "succeeded")
        && jobs4.Any(j => j.kind == "update_game" && j.state == "failed");    // steamcmd absent → honest failure
}, 30), "T14c real agent executed dispatched jobs via heartbeat ack (cleanup ok, update failed cleanly)");
var copiesLeft = new[] { Path.Combine(rootA, "Counter-Strike 2"), Path.Combine(rootB, "counter_strike_2") }.Count(Directory.Exists);
Check(copiesLeft == 1 && Directory.Exists(dupKeep),
    "T14d exactly the non-keep duplicate copy was deleted from disk");
Check(await Until(async () => (await State4()).alerts.Any(a => a.type == "job_succeeded")
    && (await State4()).alerts.Any(a => a.type == "job_failed" && a.severity == "critical"), 10),
    "T14e job outcome alerts (info success, critical failure)");

await PostAck4("/api/downed", new DownedRequest(cid4, "downed", "down-oos", "qa"));
Check(await Until(() => Task.FromResult(Wallpaper.Read(agentDir4) is { ContentId: "down-oos" }), 15),
    "T14f downed converges on agent: content cached from library + wallpaper applied");
Check(await Until(async () => (await probe4.GetAsync("/content/down-oos")).IsSuccessStatusCode, 5),
    "T14g content library served the downed wallpaper bytes");
await PostAck4("/api/downed", new DownedRequest(cid4, "active", null, null));
Check(await Until(() => Task.FromResult(Wallpaper.Read(agentDir4)?.ContentId is null), 15),
    "T14h returning to active restores the baseline wallpaper on the agent");

// ---- T24: venue wallpaper upload (settings page) is venue-wide desired state on the same real agent ----
var t24WpPng = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 11, 12, 13 };
var t24WpUp = await probe4.PostAsync("/settings/wallpaper", new ByteArrayContent(t24WpPng));
Check((int)t24WpUp.StatusCode == 200
    && await Until(() => Task.FromResult(Wallpaper.Read(agentDir4) is { ContentId: "venue-wallpaper" }), 15),
    "T24a venue wallpaper upload converges on the real agent (cached from library + applied)");
await PostAck4("/api/downed", new DownedRequest(cid4, "downed", "down-oos", "qa"));
Check(await Until(() => Task.FromResult(Wallpaper.Read(agentDir4) is { ContentId: "down-oos" }), 15),
    "T24b downed marker layers over the venue wallpaper");
await PostAck4("/api/downed", new DownedRequest(cid4, "active", null, null));
Check(await Until(() => Task.FromResult(Wallpaper.Read(agentDir4) is { ContentId: "venue-wallpaper" }), 15),
    "T24c return to active re-applies the venue wallpaper, not the old baseline");
var t24WpDel = await probe4.DeleteAsync("/settings/wallpaper");
Check((int)t24WpDel.StatusCode == 200
    && await Until(() => Task.FromResult(Wallpaper.Read(agentDir4)?.ContentId is null), 15),
    "T24d removing the venue wallpaper restores the PC's recorded baseline");
cts4.Cancel(); await loopTask4; await app4.StopAsync();

// ---------- T15: Phase 4 locate overlay pipeline units (payload → request → argv → render model) ----------
var nowT15 = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
var ovReq = new LocateOverlayRequest("LAN-PC-07", "0042", "11111111-2222-3333-4444-555555555555", nowT15 + 30, "Precision 7920 Tower");
var ovArgs = LocateOverlayRequest.BuildArgs(ovReq);
Check(ovArgs[0] == LocateOverlayRequest.Flag && ovArgs.Contains("--display-name") && ovArgs.Contains("LAN-PC-07")
    && ovArgs[ovArgs.IndexOf("--expires") + 1] == (nowT15 + 30).ToString(),
    "T15a overlay child argv is flag + typed switches (ArgumentList-safe for names with spaces)");
Check(LocateOverlayRequest.TryParse(ovArgs, out var ovBack) && ovBack == ovReq, "T15b argv round-trip parse");
Check(ovBack.ShouldShow(nowT15) && !(ovBack with { ExpiresUnix = nowT15 - 5 }).ShouldShow(nowT15)
    && !LocateOverlayRequest.TryParse([LocateOverlayRequest.Flag, "--pin", "1"], out _),
    "T15c stale overlay suppressed; malformed argv rejected (exit before render)");
Check(ovReq.QrPayload() == $"ESU:{ovReq.ClientId}:{ovReq.DevicePin}"
    && ovReq.QrPayload() == FileLabelPrinter.QrPayload(ovReq.ClientId, ovReq.DevicePin),
    "T15d overlay QR payload identical to printed label — one scan protocol covers both");
var qr15 = Qr.Modules(ovReq.QrPayload());
Check(qr15.Length >= 29 && qr15.All(r => r.Length == qr15.Length) && qr15[4][4] && qr15[4][qr15.Length - 5],
    "T15e real QR matrix with 4-module quiet zone; finder modules set at (4,4) and top-right");
Check(ConsoleOverlay.RenderCard(ovReq, nowT15) is var card && card.Contains("LAN-PC-07")
    && card.Contains("auto-dismiss in 30s") && card.Contains("ESU:11111111"),
    "T15f console fallback carries name, PIN meta, countdown and scan payload");
Check(OverlayLauncher.RequestFor(new Directive("locate", null, nowT15 + 30), new AgentIdentity("abc", "0042"), null).DisplayName == "PIN 0042",
    "T15g unnamed device falls back to PIN label (never blank overlay title)");

// ---------- T16: Phase 4 overlay layout math (mockup composition fits any viewport) ----------
foreach (var (tw, th) in new[] { (1920, 1080), (1280, 720), (3840, 2160), (1366, 768) })
{
    var ly = OverlayLayout.Compute(tw, th, 37, "LAN-PC-07", "PIN 0042 · Precision 7920 Tower · client 11111111");
    Check(ly.IdLeft >= ly.BorderInset - 0.01
        && ly.QrX + ly.QrCardPx <= tw - ly.BorderInset + 0.01          // QR card never past the border
        && ly.QrX - ly.IdLeft - ly.Gap * 0.5f >= 100                    // id block has real draw budget
        && ly.KickerY >= 0 && ly.QrY >= 0
        && ly.KickerY < ly.NameY && ly.NameY < ly.MetaY                 // vertical flow order holds
        && ly.MetaY + ly.MetaSize * 1.4f <= ly.HintY + 0.01
        && ly.QrY + ly.QrCardPx + ly.QrLabelSize * 2f <= ly.HintY + 0.01 // QR + caption clear the hint
        && ly.QrCardPx >= 37 * 3,                                       // >=3px per QR module stays scannable
        $"T16a {tw}x{th}: horizontal card composition fits, no overlaps, QR scannable");
    var longName = "STATION ZERO-NINE-ALPHA BRAVO CHARLIE DELTA ECHO";
    var ly2 = OverlayLayout.Compute(tw, th, 37, longName, "PIN 0042 · Precision 7920 Tower · client 11111111");
    var fullSize = Math.Clamp(tw * 0.075f, 30, 76 * Math.Clamp(Math.Min(tw / 1920f, th / 1080f), 0.55f, 2.2f));
    Check(0.62f * ly2.NameSize * longName.Length <= tw * 0.86f
        && (0.62f * fullSize * longName.Length > tw * 0.86f ? ly2.NameSize < fullSize : true),
        $"T16b {tw}x{th}: oversized display_name shrinks to fit beside the QR (never clips)");
}

// ---- first-run setup: role dialog / config generation ----
Check(FirstRunSetup.NormalizeServerUrl("10.140.0.212") == "http://10.140.0.212:4300" &&
    FirstRunSetup.NormalizeServerUrl(" 10.140.0.212:4300 ") == "http://10.140.0.212:4300" &&
    FirstRunSetup.NormalizeServerUrl("venue.local") == "http://venue.local:4300" &&
    FirstRunSetup.NormalizeServerUrl("http://venue.local:4301/") == "http://venue.local:4301" &&
    FirstRunSetup.NormalizeServerUrl("https://100.92.77.66:4300") == "https://100.92.77.66:4300",
    "T17a bare IP gets http scheme + default port, full URLs kept, trailing slash dropped");
Check(FirstRunSetup.NormalizeServerUrl(null) is null && FirstRunSetup.NormalizeServerUrl("  ") is null &&
    FirstRunSetup.NormalizeServerUrl("bad host!") is null && FirstRunSetup.NormalizeServerUrl("http://") is null &&
    FirstRunSetup.NormalizeServerUrl("1.2.3.4:99999") is null,
    "T17b empty/junk/malformed address never produces a config");
{
    var setupDir = Path.Combine(tmp, "first-run");
    var srv = FirstRunSetup.BuildConfig("server", null, setupDir);
    var ag = FirstRunSetup.BuildConfig("agent", "10.140.0.212", setupDir);
    Check(srv.Role == "server" && srv.ServerUrl == "http://0.0.0.0:4300" &&
        srv.DataDir == Path.Combine(setupDir, "esportsutil-data", "server") && srv.Port == 4300 &&
        ag.Role == "agent" && ag.ServerUrl == "http://10.140.0.212:4300" &&
        ag.DataDir == Path.Combine(setupDir, "esportsutil-data", "agent"),
        "T17c server config pins server_url to 0.0.0.0:4300 (all NICs); agent gets normalized server_url");
    Check(Throws<ArgumentException>(() => FirstRunSetup.BuildConfig("client", null, setupDir)) &&
        Throws<ArgumentException>(() => FirstRunSetup.BuildConfig("agent", "not a url", setupDir)),
        "T17d wizard output is validated, not trusted (bad role/address throw)");
    var written = FirstRunSetup.WriteConfig(setupDir, ag);
    var cfgBack = EsportsConfig.Load(written);
    var text = File.ReadAllText(written);
    Check(Path.GetFileName(written) == "esportsutil.config.json" &&
        cfgBack.Role == "agent" && cfgBack.ServerUrl == "http://10.140.0.212:4300" &&
        cfgBack.DataDir == ag.DataDir && text.Contains("\"server_url\""),
        "T17e generated file next to the exe round-trips through EsportsConfig.Load (same snake_case contract)");
}
{
    var srvNoUrl = new EsportsConfig { Role = "server" }; srvNoUrl.ApplyRoleOverride(null);
    var agNoUrl = new EsportsConfig(); agNoUrl.ApplyRoleOverride("agent");
    var srvPort = new EsportsConfig { Role = "server", Port = 4400 }; srvPort.ApplyRoleOverride(null);
    var srvSet = new EsportsConfig { Role = "server", ServerUrl = "http://100.92.77.66:4300" }; srvSet.ApplyRoleOverride(null);
    Check(srvNoUrl.ServerUrl == "http://0.0.0.0:4300" && agNoUrl.ServerUrl == "http://127.0.0.1:4300"
        && srvPort.ServerUrl == "http://0.0.0.0:4400" && srvSet.ServerUrl == "http://100.92.77.66:4300",
        "T17f missing server_url resolves by role: server binds 0.0.0.0 (LAN agents reach it), agent loopback; explicit URL wins");
}

// --- T18: station label artifact (name + location + logo slot; no pin/id/QR) ---
{
    var labDir = Path.Combine(AppContext.BaseDirectory, "t18-labels");
    if (Directory.Exists(labDir)) Directory.Delete(labDir, true);
    var lp = new EsportsUtil.Core.FileLabelPrinter(labDir);
    var art = lp.Print("LAN-PC-07", "Zone A · Row 2 · Seat 5");
    var html18 = File.ReadAllText(art);
    Check(Path.GetFileName(art) == "label-lan-pc-07.html", "T18a label artifact is a single-file HTML card named from the station slug");
    Check(html18.Contains(">LAN-PC-07</div>") && html18.Contains("Zone A"), "T18b label renders station name + venue location");
    Check(html18.Contains("LOGO") && html18.Contains("brand slot"), "T18c label carries the logo placeholder (filled in later) where the QR used to be");
    Check(!html18.Contains("ESU:") && !html18.Contains("0042") && !html18.Contains("11111111"), "T18d label leaks nothing machine-readable — no QR payload, no PIN, no id");
    Check(html18.Contains("size:2in 1in") && html18.Contains("508px") && html18.Contains("254px"), "T18e label is print-ready at mockup size (2x1in, zero margins)");
    var art2 = lp.Print("LAN-PC-07", null);
    var html18b = File.ReadAllText(art2);
    Check(html18b.Contains(">LAN-PC-07</div>") && !html18b.Contains("class=\"loc\""), "T18f label without a location still prints (name only, no empty loc line)");
    var art3 = lp.Print("Shea <&> Arena “Main”", "Floor 2");
    var html18c = File.ReadAllText(art3);
    Check(html18c.Contains("Shea &lt;&amp;&gt; Arena") && !html18c.Contains("<&>"), "T18g hostile station name is HTML-escaped in the label artifact");
}

// ---------- T11: resource budget gate (PLAN.md) — standalone agent, idle ----------
var perfSecs = int.TryParse(Environment.GetEnvironmentVariable("PERF_SECS"), out var ps) ? ps : 20;
var agentBin = Path.Combine(AppContext.BaseDirectory, "esportsutil.dll");
if (File.Exists(agentBin))
{
    var perfSrv = new EsportsConfig { Role = "server", ServerUrl = $"http://127.0.0.1:{port + 1}", DataDir = Path.Combine(tmp, "perf-srv") };
    using var papp = ServerRole.BuildApp(perfSrv);
    await papp.StartAsync();
    var perfAgentCfg = new EsportsConfig { Role = "agent", ServerUrl = $"http://127.0.0.1:{port + 1}", DataDir = Path.Combine(tmp, "perf-agent") };
    perfAgentCfg.HeartbeatIntervalSec = 2;
    var perfCfgPath = Path.Combine(tmp, "perf-agent.json");
    File.WriteAllText(perfCfgPath, Wire.ToJson(perfAgentCfg));
    var p = Process.Start(new ProcessStartInfo("dotnet", $"\"{agentBin}\" --role agent --config \"{perfCfgPath}\"")
    { RedirectStandardOutput = true, UseShellExecute = false })!;
    await Task.Delay(3000); // startup
    var cpu0 = ProcCpu(p.Id); var t0 = Stopwatch.GetTimestamp();
    await Task.Delay(perfSecs * 1000);
    var cpu1 = ProcCpu(p.Id); var t1 = Stopwatch.GetTimestamp();
    var cpuPct = (cpu1 - cpu0) / Stopwatch.Frequency * 100.0 / ((t1 - t0) / (double)Stopwatch.Frequency) * 100.0;
    var peakMb = ProcPeakRssMb(p.Id);
    p.Kill(entireProcessTree: true);
    Console.WriteLine($"      measured: cpu={cpuPct:F2}% over {perfSecs}s idle, peak RSS={peakMb:F1}MB");
    Check(cpuPct < 1.0, "T11a agent idle CPU < 1% (budget)");
    Check(peakMb < 60, "T11b agent working set < 60 MB (budget)");
    await papp.StopAsync();
}
else Check(false, "T11 esportsutil.dll not found next to tests — cannot measure budget");

// ---------- T22: tray + service lifecycle primitives (pure builders; runtime gates stay Windows-box deploy gates) ----------
var lc1 = Lifecycle.Parse(new[] { "service", "install", "--name", "EsportsUtilAgent" }, false, false, "EsportsUtilAgent");
Check(lc1 is { Verb: "service", SubVerb: "install", Mode: RunMode.Foreground, ServiceName: "EsportsUtilAgent" },
    "T22a service install verb parses to an ops command (never starts a role loop)");
Check(Lifecycle.Parse(Array.Empty<string>(), true, false, null).Mode == RunMode.Tray, "T22b --tray selects the tray host");
Check(Lifecycle.Parse(Array.Empty<string>(), false, true, null).Mode == RunMode.Service, "T22c --service selects the SCM service host");
Check(Lifecycle.Parse(new[] { "--role", "agent" }, false, false, null).Verb is null
    && Lifecycle.Parse(Array.Empty<string>(), false, false, null).ServiceName == "EsportsUtil",
    "T22d non-verb argv passes through; default service name is EsportsUtil");
Check(Lifecycle.KnownSubVerb("service", "install") && Lifecycle.KnownSubVerb("autostart", "status")
    && !Lifecycle.KnownSubVerb("service", "nuke") && !Lifecycle.KnownSubVerb("service", null),
    "T22e ops sub-verb gate rejects unknown/missing verbs");
var t22Bin = Lifecycle.ServiceBinPath(@"C:\Program Files\EsportsUtil\esportsutil.exe", @"C:\Program Files\EsportsUtil\esportsutil.config.json");
Check(t22Bin == "\"C:\\Program Files\\EsportsUtil\\esportsutil.exe\" --service --config \"C:\\Program Files\\EsportsUtil\\esportsutil.config.json\"",
    "T22f binPath builder quotes spaces-in-paths, pins --service, carries --config");
var t22Sc = Lifecycle.ScCreateArgs("EsportsUtil", @"C:\EsportsUtil\esportsutil.exe", null);
Check(t22Sc is ["create", "EsportsUtil", "binPath=", "\"C:\\EsportsUtil\\esportsutil.exe\" --service", "start=", "auto"],
    "T22g sc create argv shape: binPath= <quoted exe --service> start= auto");
Check(Lifecycle.RunKeyValue(@"C:\Program Files\EsportsUtil\esportsutil.exe") == "\"C:\\Program Files\\EsportsUtil\\esportsutil.exe\" --tray",
    "T22h HKCU Run value quotes the exe path and pins --tray");
var stStart = TrayStatus.ForAgent(false, false, null, null, "active");
var stBad = TrayStatus.ForAgent(true, false, null, "0042", "active");
var stPair = TrayStatus.ForAgent(true, true, null, "0042", "active");
var stDown = TrayStatus.ForAgent(true, true, "LAN-PC-01", "0042", "downed");
var stLink = TrayStatus.ForAgent(true, true, "LAN-PC-01", "0042", "active");
Check(stStart is ("starting", "gray", _) && stBad is ("offline", "red", _) && stPair is ("pairing", "amber", _)
    && stDown is ("downed", "amber", _) && stLink is ("linked", "green", _),
    "T22i tray status map: starting/offline/pairing/downed/linked drive the dot color");
Check(stLink.Tip.Contains("LAN-PC-01") && stLink.Tip.Contains("0042")
    && TrayStatus.ForServer("http://0.0.0.0:4300").Color == "green",
    "T22j tooltip carries display name + PIN; server variant is green/running");
var t22OrigOut = Console.Out;
Lifecycle.TeeConsoleToLog(Path.Combine(tmp, "logtest"));
Console.WriteLine("tee-marker-t22");
Console.SetOut(t22OrigOut);
Check(File.ReadAllText(Lifecycle.LogPath(Path.Combine(tmp, "logtest"))).Contains("tee-marker-t22"),
    "T22k TeeConsoleToLog appends console output to <dataDir>/esportsutil.log (headless modes have no console reader)");

// ---------- T23: venue logo settings (drag-drop upload, magic-bytes gate) ----------
var t23Png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4 };
var t23Jpg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 5, 6, 7 };
var t23Bmp = new byte[] { (byte)'B', (byte)'M', 1, 2, 3 };
Check(ImageFormats.Sniff(t23Png)?.Ext == "png" && ImageFormats.Sniff(t23Jpg)?.Ext == "jpg"
    && ImageFormats.Sniff(t23Bmp)?.Ext == "bmp"
    && ImageFormats.Sniff("GIF89a...."u8)?.Ext == "gif"
    && ImageFormats.Sniff(System.Text.Encoding.ASCII.GetBytes("RIFF\0\0\0\0WEBPVP8 "))?.Ext == "webp",
    "T23a sniff recognizes PNG/JPEG/BMP/GIF/WEBP by magic bytes");
Check(ImageFormats.Sniff("MZ"u8) is null && ImageFormats.Sniff("hello world"u8) is null
    && ImageFormats.Sniff(ReadOnlySpan<byte>.Empty) is null,
    "T23b sniff rejects executables, plain text and empty bodies (never trusts name/client MIME)");
var setUrl = "http://127.0.0.1:4396";
var setCfg = new EsportsConfig { Role = "server", ServerUrl = setUrl, DataDir = Path.Combine(tmp, "settings-srv") };
using var setApp = ServerRole.BuildApp(setCfg); await setApp.StartAsync();
using var setProbe = new HttpClient { BaseAddress = new Uri(setUrl), Timeout = TimeSpan.FromSeconds(5) };
var t23Up = await setProbe.PostAsync("/settings/logo", new ByteArrayContent(t23Png));
using (var upJ = JsonDocument.Parse(await t23Up.Content.ReadAsStringAsync()))
    Check((int)t23Up.StatusCode == 200
        && upJ.RootElement.GetProperty("logo").GetProperty("format").GetString() == "png"
        && upJ.RootElement.GetProperty("logo").GetProperty("bytes").GetInt32() == t23Png.Length
        && Math.Abs(upJ.RootElement.GetProperty("logo").GetProperty("updated_unix").GetInt64()
                    - DateTimeOffset.UtcNow.ToUnixTimeSeconds()) < 300,
        "T23c POST /settings/logo stores a real PNG; info reports format, size, and a plausible Unix timestamp");
var t23Got = await setProbe.GetAsync("/settings/logo");
Check((int)t23Got.StatusCode == 200 && t23Got.Content.Headers.ContentType?.MediaType == "image/png"
    && (await t23Got.Content.ReadAsByteArrayAsync()).SequenceEqual(t23Png)
    && t23Got.Headers.CacheControl?.NoStore == true,
    "T23d GET /settings/logo serves the bytes with the sniffed MIME and no-store cache");
var t23Bad = await setProbe.PostAsync("/settings/logo",
    new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes("<svg onload=alert(1)>.svg")));
var t23Big = await setProbe.PostAsync("/settings/logo", new ByteArrayContent(new byte[(int)ImageFormats.MaxLogoBytes + 1]));
var t23Del = await setProbe.DeleteAsync("/settings/logo");
var t23After = await setProbe.GetAsync("/settings/logo");
Check((int)t23Bad.StatusCode == 415 && (int)t23Big.StatusCode == 413
    && (int)t23Del.StatusCode == 200 && (int)t23After.StatusCode == 404,
    "T23e script-carrying SVG → 415, oversize → 413, DELETE then GET → 404");
await setProbe.PostAsync("/settings/logo", new ByteArrayContent(t23Png));
var t23Swap = await setProbe.PostAsync("/settings/logo", new ByteArrayContent(t23Jpg));
var t23Files = Directory.GetFiles(Path.Combine(setCfg.DataDir), "logo.*");
Check((int)t23Swap.StatusCode == 200 && t23Files.Length == 1 && t23Files[0].EndsWith(".jpg"),
    "T23f format swap leaves exactly one logo file (stale extension deleted, atomic save)");
var t23With = FileLabelPrinter.RenderHtml("LAN-PC-99", "Stage A", "data:image/png;base64,AQID");
var t23No = FileLabelPrinter.RenderHtml("LAN-PC-99", "Stage A", null);
Check(t23With.Contains("<img src=\"data:image/png;base64,AQID\"") && !t23With.Contains("brand slot")
    && t23No.Contains("brand slot"),
    "T23g label brand slot becomes an embedded <img> when a logo is set, placeholder otherwise");
var t23Page = await setProbe.GetStringAsync("/settings");
Check(t23Page.Contains("dragover") && t23Page.Contains("dataTransfer") && t23Page.Contains("/settings/logo"),
    "T23h /settings serves the page whose markup wires dragover/drop upload to the API");
using (var apiJ = JsonDocument.Parse(await setProbe.GetStringAsync("/api/settings")))
    Check(apiJ.RootElement.GetProperty("logo").GetProperty("format").GetString() == "jpg"
        && apiJ.RootElement.GetProperty("max_bytes").GetInt64() == ImageFormats.MaxLogoBytes,
        "T23i GET /api/settings exposes current logo info + the 4 MB cap for client-side UX checks");
var t23Del2 = await setProbe.DeleteAsync("/settings/logo");
var t23Del3 = await setProbe.DeleteAsync("/settings/logo");
Check((int)t23Del2.StatusCode == 200 && (int)t23Del3.StatusCode == 404,
    "T23j second DELETE (nothing left) → 404 ack envelope, never throws");

// ---------- T24: venue wallpaper storage shape (settings upload → Content Library desired state) ----------
var t24Wp = await setProbe.PostAsync("/settings/wallpaper", new ByteArrayContent(t23Png));
using (var wj = JsonDocument.Parse(await t24Wp.Content.ReadAsStringAsync()))
    Check((int)t24Wp.StatusCode == 200
        && wj.RootElement.GetProperty("wallpaper").GetProperty("format").GetString() == "png",
        "T24e POST /settings/wallpaper accepts the same image set as the logo and reports it back");
using (var mj = JsonDocument.Parse(File.ReadAllText(Path.Combine(setCfg.DataDir, "ContentManifest.json"))))
{
    var vi = mj.RootElement.GetProperty("items").EnumerateArray().Single();
    Check(vi.GetProperty("content_id").GetString() == "venue-wallpaper"
        && vi.GetProperty("category").GetString() == "wallpaper"
        && vi.GetProperty("filename").GetString() == "venue-wallpaper.png"
        && vi.GetProperty("sha256").GetString() == Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(t23Png)).ToLowerInvariant()
        && new FileInfo(Path.Combine(setCfg.DataDir, "content", "venue-wallpaper.png")).Length == t23Png.Length,
        "T24f upload lands as Content Library item venue-wallpaper (correct sha256, category, bytes on disk)");
}
var t24Exe = await setProbe.PostAsync("/settings/wallpaper", new ByteArrayContent(new byte[] { (byte)'M', (byte)'Z', 1 }));
var t24BigWp = await setProbe.PostAsync("/settings/wallpaper", new ByteArrayContent(new byte[(int)ImageFormats.MaxWallpaperBytes + 1]));
var t24WpGet = await setProbe.GetAsync("/settings/wallpaper");
Check((int)t24Exe.StatusCode == 415 && (int)t24BigWp.StatusCode == 413
    && (int)t24WpGet.StatusCode == 200 && t24WpGet.Content.Headers.ContentType?.MediaType == "image/png"
    && (await t24WpGet.Content.ReadAsByteArrayAsync()).SequenceEqual(t23Png)
    && t24WpGet.Headers.CacheControl?.NoStore == true,
    "T24g wallpaper gate: executable → 415, oversize → 413; GET serves exact bytes, sniffed MIME, no-store");
var t24Page = await setProbe.GetStringAsync("/settings");
Check(t24Page.Contains("/settings/wallpaper") && t24Page.Contains("Desktop wallpaper")
    && t24Page.Contains("drop-wp"),
    "T24h settings page markup carries a second drop slot wired to /settings/wallpaper");
var t24Del = await setProbe.DeleteAsync("/settings/wallpaper");
var t24Del2 = await setProbe.DeleteAsync("/settings/wallpaper");
using (var apiW = JsonDocument.Parse(await setProbe.GetStringAsync("/api/settings")))
    Check((int)t24Del.StatusCode == 200 && (int)t24Del2.StatusCode == 404
        && !apiW.RootElement.TryGetProperty("wallpaper", out _)
        && apiW.RootElement.GetProperty("max_wallpaper_bytes").GetInt64() == ImageFormats.MaxWallpaperBytes
        && !File.Exists(Path.Combine(setCfg.DataDir, "content", "venue-wallpaper.png")),
        "T24i delete removes item+file, second delete 404, /api/settings reports none + 8 MB cap");

Directory.Delete(tmp, true);
Console.WriteLine($"\n{(failed == 0 ? "ALL TESTS PASSED" : $"{failed} TEST(S) FAILED")}");
return failed;

static double ProcCpu(int pid)
{
    var f = File.ReadAllText($"/proc/{pid}/stat").Split(' ');
    return (long.Parse(f[13]) + long.Parse(f[14])) / 100.0; // utime+stime, USER_HZ=100
}
static double ProcPeakRssMb(int pid)
{
    foreach (var line in File.ReadAllLines($"/proc/{pid}/status"))
        if (line.StartsWith("VmHWM:")) return double.Parse(line.Split(default(char[]), StringSplitOptions.RemoveEmptyEntries)[1]) / 1024.0;
    return double.MaxValue;
}
static bool Throws<T>(Action a) where T : Exception
{
    try { a(); return false; } catch (T) { return true; }
}

// minimal shape for reading /api/state
file sealed record JsonDoc(List<Dev> devices, List<AlertDoc> alerts);
file sealed record Dev(string client_id, int? games, int? duplicates, List<IssueDoc> issues, List<JobDoc>? jobs, string? display_name = null, string? location = null, string? device_pin = null);

/// <summary>Fake sticky-overlay child for T21: Alive flips when staff "bypasses" on the machine;
/// WasKilled records the operator-driven clear. The agent loop supervises this handle each ack.</summary>
file sealed class DownProbe : EsportsUtil.Agent.IDownOverlayHandle
{
    public bool AliveFlag = true;
    public bool WasKilled;
    public bool Alive => AliveFlag;
    public void Kill() { if (AliveFlag) { AliveFlag = false; WasKilled = true; } }
}
file sealed record IssueDoc(string kind, string subject, string? keep_path, bool? @protected);
file sealed record JobDoc(string job_id, string kind, string subject, string state, string? detail);
file sealed record AlertDoc(string type, string severity);

file sealed class RecordingNotifier(List<string> log) : EsportsUtil.Server.INotifier
{
    public Task NotifyAsync(Alert a, CancellationToken ct)
    {
        log.Add(a.Type);
        if (a.Type == "boom") throw new InvalidOperationException("simulated toast host failure");
        return Task.CompletedTask;
    }
}
