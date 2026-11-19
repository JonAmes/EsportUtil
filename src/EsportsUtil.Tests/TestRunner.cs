using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
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
agCfg.HeartbeatIntervalSec = 1; agCfg.SlowScanIntervalMin = 0; agCfg.UpdateScanIntervalMin = 0;  // timer scans off: WU polls only via explicit T25 wiring (keeps report-hash dedupe tests deterministic)
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
agCfg2.HeartbeatIntervalSec = 1; agCfg2.SlowScanIntervalMin = 0; agCfg2.UpdateScanIntervalMin = 0; agCfg2.ScanDirs = new List<string> { rootA, rootB };
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
agCfg4.HeartbeatIntervalSec = 1; agCfg4.SlowScanIntervalMin = 0; agCfg4.UpdateScanIntervalMin = 0; agCfg4.ScanDirs = new List<string> { rootA, rootB };
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
    perfAgentCfg.AgentDashboardPort = 0;   // ephemeral: the gate must not collide with a dev/demo agent holding 4310
    // Absolute path to an existing stub → locate succeeds from config: the footprint gate
    // measures the idle agent, not System.Diagnostics.Process pages pulled in by a PATH probe.
    var stubCmd = Path.Combine(tmp, "steamcmd-stub"); File.WriteAllText(stubCmd, "");
    perfAgentCfg.SteamCmdPath = stubCmd;
    perfAgentCfg.SteamCmdAutoInstall = false;
    perfAgentCfg.SelfUpdateCheckIntervalMin = 0;   // footprint gate must not pay for GitHub TLS buffers
    var perfCfgPath = Path.Combine(tmp, "perf-agent.json");
    File.WriteAllText(perfCfgPath, Wire.ToJson(perfAgentCfg));
    var psi = new ProcessStartInfo("dotnet", $"\"{agentBin}\" --role agent --config \"{perfCfgPath}\"")
    { RedirectStandardOutput = true, UseShellExecute = false };
    psi.Environment["ESU_FAKE_APP_VERSIONS"] = "chrome=157.0.0.0,firefox=160.0";   // idle gate measures footprint, not TLS buffers (same seam style as ESU_FAKE_UPDATES)
    var p = Process.Start(psi)!;
    await Task.Delay(3000); // startup
    var cpu0 = ProcCpu(p.Id); var t0 = Stopwatch.GetTimestamp();
    await Task.Delay(perfSecs * 1000);
    var cpu1 = ProcCpu(p.Id); var t1 = Stopwatch.GetTimestamp();
    var cpuPct = (cpu1 - cpu0) / Stopwatch.Frequency * 100.0 / ((t1 - t0) / (double)Stopwatch.Frequency) * 100.0;
    var peakMb = ProcPeakRssMb(p.Id);
    p.Kill(entireProcessTree: true);
    Console.WriteLine($"      measured: cpu={cpuPct:F2}% over {perfSecs}s idle, peak RSS={peakMb:F1}MB");
    Check(cpuPct < 1.0, "T11a agent idle CPU < 1% (budget)");
    Check(peakMb < 64, "T11b agent working set < 64 MB (budget amended for WU COM + steamcmd + catalog + self-update floor)");
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

// ---------- T25: Windows Update polling — timed interval AND operator-requested → report → registry → pill ----------
Environment.SetEnvironmentVariable(UpdateWatch.FakeEnvVar, "3");   // cross-OS scan seam (PLAN: real WUA is a Windows gate)
Check(UpdateWatch.Due(1000, null, 360, false, false) && UpdateWatch.Due(1000, 1000 - 360 * 60, 360, false, false)
    && !UpdateWatch.Due(1000, 1000 - 359 * 60, 360, false, false) && !UpdateWatch.Due(1000, null, 0, false, false)
    && UpdateWatch.Due(1000, null, 0, false, true) && !UpdateWatch.Due(1000, null, 360, true, true),
    "T25a Due(): never-scanned due; interval lapsed due; fresh scan not; timer-off only scans on request; in-flight scan blocks re-entry");
await UpdateWatch.ScanAsync();
Check(UpdateWatch.Last is { Status: "updates", PendingCount: 3 } && UpdateWatch.Last!.LastScanUnix > 0,
    "T25b fake scan (ESU_FAKE_UPDATES=3) yields 'updates'/3 with a fresh scan timestamp");
var t25Rec = StateModule.Record();
Check(t25Rec["windowsUpdate"]?["pending_count"]?.GetValue<int>() == 3
    && t25Rec["windowsUpdate"]!["status"]!.GetValue<string>() == "updates",
    "T25c State Module embeds the cached windowsUpdate summary into machine_state (single writer)");
Environment.SetEnvironmentVariable(UpdateWatch.FakeEnvVar, null);
await UpdateWatch.ScanAsync();
Check(OperatingSystem.IsWindows() || (UpdateWatch.Last!.Status == "unsupported" && UpdateWatch.Last!.PendingCount is null),
    "T25d no fake hook on a non-Windows box → 'unsupported', never throws");

// live pair: directive-requested poll and timer poll both flow agent → report → registry → /api/state
var srvUrl5 = "http://127.0.0.1:4399"; var srvDir5 = Path.Combine(tmp, "srv5");
var srvCfg5 = new EsportsConfig { Role = "server", ServerUrl = srvUrl5, DataDir = srvDir5 };
using var app5 = ServerRole.BuildApp(srvCfg5); await app5.StartAsync();
using var probe5 = new HttpClient { BaseAddress = new Uri(srvUrl5), Timeout = TimeSpan.FromSeconds(5) };
var agCfg5 = new EsportsConfig { Role = "agent", ServerUrl = srvUrl5, DataDir = Path.Combine(tmp, "agent5") };
agCfg5.HeartbeatIntervalSec = 1; agCfg5.SlowScanIntervalMin = 0; agCfg5.UpdateScanIntervalMin = 0;   // timer driven explicitly below
agCfg5.ScanDirs = new List<string> { rootA };
using var cts5 = new CancellationTokenSource();
var loop5 = new AgentLoop(new HttpClient { Timeout = TimeSpan.FromSeconds(15) }, agCfg5);
var loopTask5 = loop5.RunAsync(cts5.Token);
string cid5 = "";
await Until(async () => { cid5 = (await State5()).devices.FirstOrDefault()?.client_id ?? ""; return cid5.Length > 0; }, 20);
async Task<JsonDoc> State5() => Wire.FromJson<JsonDoc>(await probe5.GetStringAsync("/api/state"))!;
var t25Nope = await probe5.PostAsync("/api/scan-updates/nope", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
Environment.SetEnvironmentVariable(UpdateWatch.FakeEnvVar, "5");
var t25Req = await probe5.PostAsync($"/api/scan-updates/{cid5}", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
Check((int)t25Nope.StatusCode == 404 && (int)t25Req.StatusCode == 200
    && await Until(async () => { var d = (await State5()).devices.First(x => x.client_id == cid5); return d.wu_pending == 5 && d.wu_status == "updates"; }, 15),
    "T25e operator-requested poll: /api/scan-updates queues a scan_updates directive (unknown device 404); agent scans on its beat and wu_pending/status land on /api/state");
agCfg5.UpdateScanIntervalMin = 1;   // live config object — the loop re-reads the cadence each beat
Environment.SetEnvironmentVariable(UpdateWatch.FakeEnvVar, "7");
UpdateWatch.Reset();                // never-scanned again → timer Due fires with NO request in flight
Check(await Until(async () => (await State5()).devices.First(x => x.client_id == cid5).wu_pending == 7, 15),
    "T25f timed-interval poll: with interval lapsed and nothing requested, the loop re-polls on its own beat and the new count flows to /api/state");
Environment.SetEnvironmentVariable(UpdateWatch.FakeEnvVar, null); UpdateWatch.Reset();
var t25Dash = await probe5.GetStringAsync("/");
Check(t25Dash.Contains(".pill.yel") && t25Dash.Contains("#eab308") && t25Dash.Contains("animation:pulse")
    && t25Dash.Contains("/api/scan-updates/") && t25Dash.Contains("wu_pending") && t25Dash.Contains("scanUpdates("),
    "T25g dashboard markup: yellow (#eab308) pulsing state pill on wu_pending>0 + per-device scan-updates wiring");

// ---------- T26: wallpaper drift detection on the beat + sync pill re-applies the enforced image ----------
// Same live pair (agent5 + app5). Linux dev surface: the desktop's "current wallpaper" is the
// Current path in agent5's wallpaper-state.json (Wallpaper.Apply wrote it), so corrupting the
// applied cache file IS the drift signal the Windows registry path would give.
var agDir5 = agCfg5.DataDir;
var t26Up = await probe5.PostAsync("/settings/wallpaper", new ByteArrayContent(t23Png));
Check((int)t26Up.StatusCode == 200
    && await Until(async () => Wallpaper.Read(agDir5)?.Current is { Length: > 0 } c && File.Exists(c), 20),
    "T26a uploading a venue wallpaper on the server makes agent5 download + apply it (content pipeline, idempotent per key)");
var t26Path = Wallpaper.Read(agDir5)!.Current!;
var t26Sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(t26Path))).ToLowerInvariant();
Check(t26Sha == Convert.ToHexString(SHA256.HashData(t23Png)).ToLowerInvariant()
    && await Until(async () => !(await State5()).devices.First(x => x.client_id == cid5).wallpaper_drift, 10),
    "T26b in-sync report: expected sha = venue image sha, wallpaper_drift false on /api/state");
File.AppendAllText(t26Path, "\u0000");   // simulate tamper/user change of the enforced file
Check(await Until(async () => (await State5()).devices.First(x => x.client_id == cid5).wallpaper_drift, 15),
    "T26c drift detected on the next beat: changed file hash ≠ enforced sha → flip forces a prompt report → wallpaper_drift true");
var t26Nope = await probe5.PostAsync("/api/sync-wallpaper/nope", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
var t26Sync = await probe5.PostAsync($"/api/sync-wallpaper/{cid5}", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
Check((int)t26Nope.StatusCode == 404 && (int)t26Sync.StatusCode == 200
    && await Until(async () =>
    {
        var d = (await State5()).devices.First(x => x.client_id == cid5);
        return !d.wallpaper_drift
            && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Wallpaper.Read(agDir5)!.Current!))).ToLowerInvariant() == t26Sha;
    }, 20),
    "T26d sync pill wiring: POST /api/sync-wallpaper (unknown device 404) queues sync_wallpaper → agent re-validates cache against the manifest sha (corrupted file re-downloaded, self-healed) and drift clears");
var t26Del = await probe5.DeleteAsync("/settings/wallpaper");
Check((int)t26Del.StatusCode == 200
    && await Until(async () =>
    {
        var d = (await State5()).devices.First(x => x.client_id == cid5);
        return !d.wallpaper_drift && WallpaperWatch.ExpectedSha256 is null && Wallpaper.Read(agDir5)?.Current is null;
    }, 20),
    "T26e removing the venue wallpaper restores the baseline, un-enforces drift (expected sha cleared), pill logic stays quiet");
var t26Dash = await probe5.GetStringAsync("/");
Check(t26Dash.Contains(".btn.yel") && t26Dash.Contains("animation:pulse") && t26Dash.Contains("wallpaper_drift")
    && t26Dash.Contains("syncWallpaper(") && t26Dash.Contains("/api/sync-wallpaper/"),
    "T26f dashboard markup: yellow pulsing sync pill-button rendered only on wallpaper_drift, wired to /api/sync-wallpaper");
Check(t26Dash.Contains("id=\"off-modal\"") && t26Dash.Contains("id=\"off-pin\"")
    && t26Dash.Contains("re-type the station's PIN") && t26Dash.Contains("PIN does not match")
    && t26Dash.Contains("confirmOffboard(") && !t26Dash.Contains("if (!confirm("),
    "T27 offboarding opens a modal demanding PIN re-entry (blocked on mismatch) instead of the one-Enter confirm()");
WallpaperWatch.Reset();
cts5.Cancel(); await loopTask5; await app5.StopAsync();

// ---- T28: steamcmd locate + winget bootstrap + default Steam roots ----
var t28Exists = new HashSet<string>(
    new[] { @"C:\games\steamcmd.exe", @"C:\winget\links\steamcmd.exe", @"C:\winget\pkg\steamcmd.exe" },
    StringComparer.Ordinal);
var (t28p1, t28s1) = SteamCmdWatch.Find(@"C:\games\steamcmd.exe", t28Exists.Contains,
    @"C:\winget\links\steamcmd.exe", @"C:\winget\pkg\steamcmd.exe", true);
var (t28p2, t28s2) = SteamCmdWatch.Find("steamcmd", t28Exists.Contains,   // relative config value: never trusted
    @"C:\winget\links\steamcmd.exe", null, true);
var (t28p3, t28s3) = SteamCmdWatch.Find(null, t28Exists.Contains, null, @"C:\winget\pkg\steamcmd.exe", true);
var (t28p4, t28s4) = SteamCmdWatch.Find(null, _ => false, null, null, false);
Check(t28p1 == @"C:\games\steamcmd.exe" && t28s1 == "config"
    && t28p2 == @"C:\winget\links\steamcmd.exe" && t28s2 == "winget_link"
    && t28p3 == @"C:\winget\pkg\steamcmd.exe" && t28s3 == "winget_package"
    && t28p4 is null && t28s4 is null,
    "T28a steamcmd locate precedence: absolute config path > winget link > winget portable package > PATH; relative config values ignored");
var t28Pkg = SteamCmdWatch.PickPortablePackage("C:/winget/packages",
    _ => new[] { "Valve.SteamCMD_Microsoft.Winget.Source_8wekyb3d8bbwe", "SomeOther.Package_1" },
    p => p.Contains("Valve.SteamCMD_") && p.EndsWith("steamcmd.exe"));
var t28PkgNone = SteamCmdWatch.PickPortablePackage("C:/winget/packages",
    _ => new[] { "SomeOther.Package_1" }, _ => true);
Check(t28Pkg is not null && t28Pkg.EndsWith("steamcmd.exe") && t28PkgNone is null,
    "T28b portable package pick matches only the Valve.SteamCMD_* dir AND demands a real steamcmd.exe inside");
const long t28Now = 1_800_000_000L;
Check(SteamCmdWatch.EnsureDue(t28Now, null, false, false, true)
    && SteamCmdWatch.EnsureDue(t28Now, t28Now - 31 * 60, false, false, true)
    && !SteamCmdWatch.EnsureDue(t28Now, t28Now - 60, false, false, true)          // inside retry window
    && !SteamCmdWatch.EnsureDue(t28Now, null, true, false, true)                  // already found
    && !SteamCmdWatch.EnsureDue(t28Now, null, false, true, true)                  // one in flight
    && !SteamCmdWatch.EnsureDue(t28Now, null, false, false, false),               // operator opted out
    "T28c bootstrap policy: attempt only when missing, idle, opted in, and first time or >=30 min since last attempt");
Check(SteamCmdWatch.InstallOutcome(null, true) == "run"
    && SteamCmdWatch.InstallOutcome(null, false) == "unavailable"
    && SteamCmdWatch.InstallOutcome("ok", false) == "installed"
    && SteamCmdWatch.InstallOutcome("fail", true) == "failed"
    && SteamCmdWatch.InstallOutcome("surprise", true) == "failed"
    && string.Join(' ', SteamCmdWatch.WingetArgs()) is var t28Wa
    && t28Wa.Contains("-e --id Valve.SteamCMD") && t28Wa.Contains("--accept-package-agreements")
    && t28Wa.Contains("--silent") && t28Wa.Contains("--disable-interactivity"),
    "T28d bootstrap decision + winget argv: run on Windows, 'unavailable' elsewhere (never throws), seams honored, identity-pinned fully-unattended install");
var t28Dirs = GameScanner.EffectiveScanDirs(new[] { "/games", "  ", "/steam/library" }, "/steam/common");
var t28Dedupe = GameScanner.EffectiveScanDirs(new[] { "/Games/", "/games" }, "/GAMES");
var t28NoSteam = GameScanner.EffectiveScanDirs(new[] { "/games" }, "");
Check(t28Dirs.SequenceEqual(new[] { "/games", "/steam/library", "/steam/common" })
    && t28Dedupe.SequenceEqual(new[] { "/Games/" })
    && t28NoSteam.SequenceEqual(new[] { "/games" }),
    "T28e effective scan dirs: default Steam root auto-appended when present, case-insensitive dedupe vs configured roots, blanks dropped");
Environment.SetEnvironmentVariable("ESU_FAKE_WINGET", "fail");
SteamCmdWatch.Reset();
var t28Cfg = new EsportsConfig { Role = "agent", DataDir = Path.Combine(tmp, "t28") };
var t28First = await SteamCmdWatch.CheckAsync(t28Cfg);
var t28Failed = await Until(async () => SteamCmdWatch.Last?.InstallState == "failed", 5);
var t28Carry = await SteamCmdWatch.CheckAsync(t28Cfg);   // retry window not elapsed: verdict carried, no second attempt
Check(t28First.InstallState == "installing" && t28Failed
    && t28Carry is { Found: false, InstallState: "failed" }
    && StateModule.Record()["steamCmd"]?["install_state"]?.GetValue<string>() == "failed",
    "T28f beat seam: missing steamcmd kicks the winget bootstrap (installing), a failed bootstrap carries 'failed' into later checks without re-attempting, and the verdict rides machine_state.steamCmd");
Environment.SetEnvironmentVariable("ESU_FAKE_WINGET", null);
SteamCmdWatch.Reset();

// ---------- T29: venue app installs (Chrome/Firefox/Steam/Epic/Battle.net/Riot) ----------
Check(AppCatalog.All.Count == 6
    && string.Join(',', AppCatalog.All.Select(e => e.Id)) == "chrome,firefox,steam,epic,battlenet,riot"
    && AppCatalog.All.All(e => e.InstallerUrl.StartsWith("https://", StringComparison.Ordinal))
    && AppCatalog.All.All(e => e.Name.Length > 0)
    && AppCatalog.Entry("Chrome") is not null && AppCatalog.Entry("nope") is null
    && AppCatalog.IsMsiUrl(AppCatalog.Entry("chrome")!.InstallerUrl)
    && !AppCatalog.IsMsiUrl("https://down-www-us.blizzard.com/download/b.net/index.php?platform=win")
    && !AppCatalog.IsMsiUrl(AppCatalog.Entry("riot")!.InstallerUrl),
    "T29a catalog: six venue apps, direct https vendor URLs only, case-insensitive lookup, MSI decision reads the path not the query");
Check(AppCatalog.VersionCompare("157.0.8103.79", "157.0.9999.1") < 0
    && AppCatalog.VersionCompare("1.2.10", "1.2.9") > 0
    && AppCatalog.VersionCompare("121", "121.0") == 0
    && AppCatalog.Outdated("120.0", "121.0") == true
    && AppCatalog.Outdated("121.0", "121.0") == false
    && AppCatalog.Outdated("121.0", null) is null
    && AppCatalog.Outdated(null, "121.0") is null,
    "T29b version model: dot-numeric compare; outdated=true only when both sides known and behind; self-updating launchers report indeterminate (null), never fake-green");
var t29Inv = JsonNode.Parse("""[{"display_name":"Google Chrome","display_version":"120.0.0.0"},{"display_name":"Battle.net","display_version":"2.0.0"},{"display_name":"Steam","display_version":null}]""");
var t29St = AppCatalog.Statuses(t29Inv, id => id == "chrome" ? "121.0.0.0" : null, 555);
var t29Node = AppCatalog.ToNode(t29St);
bool t29Flag(string id, string key) => t29Node.First(a => a!["app_id"]!.GetValue<string>() == id)![key]!.GetValue<bool>();
string t29Ver(string id) => t29Node.First(a => a!["app_id"]!.GetValue<string>() == id)!["version"]!.GetValue<string>();
bool t29IsNull(string id, string key) { var v = t29Node.First(a => a!["app_id"]!.GetValue<string>() == id)![key]; return v is null || v.GetValueKind() == JsonValueKind.Null; }
Check(t29St.Count == 6 && t29St.All(s => s.CheckedUnix == 555)
    && t29Flag("chrome", "installed") && t29Flag("chrome", "up_to_date") == false
    && t29Ver("chrome") == "120.0.0.0"
    && t29Flag("battlenet", "installed") && t29IsNull("battlenet", "up_to_date")
    && t29Flag("steam", "installed") && t29IsNull("steam", "up_to_date")   // installed, version null → no fake verdict
    && t29IsNull("epic", "up_to_date"),
    "T29c apps projection: one row per catalog entry with stable checked_unix; registry-name mapping (Battle.net), outdated only vs known latest, not-installed rows indeterminate");
Check(AppCatalog.ProgressPercent(512, 1024) == 50
    && AppCatalog.ProgressPercent(2048, 1024) == 100
    && AppCatalog.ProgressPercent(10, -1) is null,
    "T29d progress percent: known total → clamped 0..100; unknown Content-Length → null (indeterminate bar, job still runs)");
Check(AppCatalog.ParseLatest("chrome", """{"channel":"stable","version":"157.0.0.0","revision":"r"}""") == "157.0.0.0"
    && AppCatalog.ParseLatest("firefox", """{"LATEST_FIREFOX_VERSION":"160.0","FIREFOX_ESR":"128.0"}""") == "160.0"
    && AppCatalog.ParseLatest("steam", "{}") is null,
    "T29e vendor latest-version parsers: Chrome versionhistory + Firefox product-details JSON; app without an API → null");
var t29Store = new JobStore(Path.Combine(tmp, "t29jobs"));
var t29Job = t29Store.Enqueue("c-t29", "install_app", "firefox", null, null);
Check(t29Store.Update(t29Job.JobId, "c-t29", "running", "1 MB downloaded", 40)!.Progress == 40
    && t29Store.Update(t29Job.JobId, "c-t29", "running", "2 MB downloaded")!.Progress == 40
    && t29Store.Update(t29Job.JobId, "c-t29", "succeeded", "done")!.Progress == 40,
    "T29f job progress rides updates: percent persists when a later update omits it, survives to the terminal state");
var t29Runner = new JobRunner(new HttpClient { Timeout = TimeSpan.FromSeconds(5) },
    new EsportsConfig { Role = "agent", ServerUrl = "http://127.0.0.1:9", DataDir = Path.Combine(tmp, "t29ag") });
var t29Dl = await t29Runner.DownloadWithProgressAsync("http://example.invalid/x.exe", Path.Combine(tmp, "t29dl.exe"), t29Job, default);
Check(!t29Dl.Ok && t29Dl.Error!.Contains("https") && !File.Exists(Path.Combine(tmp, "t29dl.exe")),
    "T29g direct-download guard: non-https installer URLs refused before any file is written");
var srvUrl6 = "http://127.0.0.1:4400"; var srvDir6 = Path.Combine(tmp, "srv6");
var srvCfg6 = new EsportsConfig { Role = "server", ServerUrl = srvUrl6, DataDir = srvDir6 };
using var app6 = ServerRole.BuildApp(srvCfg6); await app6.StartAsync();
using var probe6 = new HttpClient { BaseAddress = new Uri(srvUrl6), Timeout = TimeSpan.FromSeconds(5) };
_ = await probe6.PostAsync("/api/register", new StringContent(Wire.ToJson(new RegisterRequest("c-t29reg", "8888", "Install Bench")), System.Text.Encoding.UTF8, "application/json"));
async Task<(int Code, JsonObject Body)> Post6(string path, object? body = null)
{
    var r = await probe6.PostAsync(path, new StringContent(Wire.ToJson(body ?? new { }), System.Text.Encoding.UTF8, "application/json"));
    return ((int)r.StatusCode, JsonNode.Parse(await r.Content.ReadAsStringAsync())!.AsObject());
}
var t29Cat = JsonNode.Parse(await probe6.GetStringAsync("/api/app-catalog"))!.AsArray();
var (t29C404, _) = await Post6("/api/install-app/ghost", new AppInstallRequest("chrome"));
var (t29C400, t29E400) = await Post6("/api/install-app/c-t29reg", new AppInstallRequest("photoshop"));
var (t29C200, t29J200) = await Post6("/api/install-app/c-t29reg", new AppInstallRequest("firefox"));
var (t29C409, _) = await Post6("/api/install-app/c-t29reg", new AppInstallRequest("firefox"));
var (t29CUpd0, _) = await Post6("/api/update-app/c-t29reg", new AppInstallRequest("chrome"));
Check(t29Cat.Count == 6 && t29C404 == 404 && t29C400 == 400
    && t29E400["error"]!.GetValue<string>().Contains("photoshop")
    && t29C200 == 200 && t29J200["ok"]!.GetValue<bool>() && t29J200["job"]!["job_id"]!.GetValue<string>().Length > 0
    && t29C409 == 409 && t29CUpd0 == 400,
    "T29h install/update routes: catalog served; ghost 404; unknown app 400; success queues exactly one job in ack shape; duplicate open job 409; update before any app report 400");
var t29Ms = new JsonObject { ["apps"] = AppCatalog.ToNode(AppCatalog.Statuses(
    JsonNode.Parse("""[{"display_name":"Google Chrome","display_version":"120.0.0.0"}]"""), id => id == "chrome" ? "121.0" : null, 777)) };
// T32: same report also carries the raw four-view inventory, uninstall strings included.
t29Ms["installedApps"] = JsonNode.Parse("""[{"registry_key":"ChromeHTML","display_name":"Google Chrome","publisher":"Google LLC","display_version":"120.0.0.0","install_location":"C:\\\\Program Files\\\\Google\\\\Chrome","uninstall_string":"msiexec.exe /x {ABCD}","quiet_uninstall_string":"msiexec.exe /x {ABCD} /q","hive":"HKCU","view":"64"},{"registry_key":"Steam","display_name":"Steam","publisher":"Valve Corporation","display_version":"2.10.91","uninstall_string":"\\\\Steam\\\\uninstall.exe","hive":"HKLM","view":"32"}]""");
var (t29CRep, _) = await Post6("/api/report", new LocalManifest("c-t29reg", "8888", "Install Bench",
    DateTimeOffset.UtcNow, "test", t29Ms, new List<GameReport>()));
var t29Dev6 = Wire.FromJson<JsonDoc>(await probe6.GetStringAsync("/api/state"))!
    .devices.First(d => d.client_id == "c-t29reg");
Check(t29CRep == 200 && t29Dev6.apps is { Count: 6 }
    && t29Dev6.apps!.First(a => a.app_id == "chrome") is { installed: true, up_to_date: false, version: "120.0.0.0" }
    && t29Dev6.apps!.All(a => a.checked_unix == 777),
    "T29i machine_state.apps → registry → /api/state: six rows projected; chrome 120 vs vendor 121 reads installed+outdated on the server");
var t32Chrome = t29Dev6.installed_apps!.First(a => a.name == "Google Chrome");
var t32Steam = t29Dev6.installed_apps!.First(a => a.name == "Steam");
var t32Raw6 = await probe6.GetStringAsync("/api/state");
Check(t29Dev6.installed_apps is { Count: 2 } && t32Chrome.hive == "HKCU" && t32Chrome.view == "64" && t32Chrome.version == "120.0.0.0"
    && t32Steam.hive == "HKLM" && t32Steam.view == "32" && t32Steam.publisher == "Valve Corporation"
    && !t32Raw6.Contains("msiexec") && !t32Raw6.Contains("uninstall_string") && !t32Raw6.Contains("registry_key"),
    "T32a machine_state.installedApps → registry → /api/state: full inventory projected as installed_apps (name/publisher/version/hive/view, sorted); uninstall strings + registry keys deliberately never leave the agent");
var (t29CUpd1, t29JUpd1) = await Post6("/api/update-app/c-t29reg", new AppInstallRequest("chrome"));
var (t29CUpd2, _) = await Post6("/api/update-app/c-t29reg", new AppInstallRequest("epic"));
Check(t29CUpd1 == 200 && t29JUpd1["ok"]!.GetValue<bool>() && t29JUpd1["job"]!["kind"]!.GetValue<string>() == "update_app"
    && t29CUpd2 == 400,
    "T29j per-app update: allowed exactly for apps the machine reported installed (outdated chrome → queued); not-installed epic refused");
var t29SetHtml = File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
    "EsportsUtil", "wwwroot", "settings.html")));
Check(t29SetHtml.Contains("Venue app installs") && t29SetHtml.Contains("id=\"dev\"")
    && t29SetHtml.Contains("/api/app-catalog") && t29SetHtml.Contains("'-app/'")
    && t29SetHtml.Contains("class=\"bar\"") && t29SetHtml.Contains("job.progress"),
    "T29k settings page ships per-app install/update buttons and live progress bars fed by job progress");
var dashHtmlLate = File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
    "EsportsUtil", "wwwroot", "dashboard.html")));
Check(dashHtmlLate.Contains("pushAppUpdate") && dashHtmlLate.Contains("up_to_date === false")
    && dashHtmlLate.Contains("j.progress!=null"),
    "T29l dashboard ships the clickable outdated-app chip and job percent");
// ===== T30 self-update (PLAN.md): GitHub release watcher, staged-EXE handoff, tray auto-load =====
{
    SelfUpdateWatch.Reset();
    Check(SelfUpdateWatch.NormalizeTag("v0.5.1") == "0.5.1", "T30a release tag v-prefix normalized");
    Check(SelfUpdateWatch.NewerThan("0.5.0", "0.5.1") && !SelfUpdateWatch.NewerThan("0.5.0", "0.5.0")
        && !SelfUpdateWatch.NewerThan("0.6.0", "0.5.9") && SelfUpdateWatch.NewerThan("0.5.0", "0.10.0"),
        "T30a newer-than is numeric semver, never lexicographic");
    Check(SelfUpdateWatch.Due(3600, 0, 60) && !SelfUpdateWatch.Due(3599, 0, 60) && !SelfUpdateWatch.Due(999999, 0, 0),
        "T30b check cadence honors window; 0 disables timed checks");
    var rel30 = SelfUpdateWatch.ParseRelease("""
        {"tag_name":"v9.9.9","assets":[
          {"name":"esportsutil.pdb","browser_download_url":"https://x/pdb","size":1},
          {"name":"esportsutil.exe","browser_download_url":"https://github.example/raw/esportsutil.exe","size":52428800,"digest":"sha256:abcd"}]}
        """);
    Check(rel30 is not null && rel30.Tag == "v9.9.9" && rel30.Size == 52428800 && rel30.Sha256 == "abcd"
        && rel30.DownloadUrl.EndsWith("esportsutil.exe"),
        "T30c release payload parsed: exe asset picked over decoys, sha digest captured");
    Check(SelfUpdateWatch.ParseRelease("""{"tag_name":"v1","assets":[]}""") is null
        && SelfUpdateWatch.ParseRelease("{not json") is null,
        "T30c missing/garbage payload -> null: never update to nothing");
    var fa30 = SelfUpdateWatch.FinalizeArgs(@"C:\Program Files\EsportsUtil\esportsutil.exe", 4242);
    Check(fa30[0] == SelfUpdateWatch.FinalizeFlag && fa30[1] == @"C:\Program Files\EsportsUtil\esportsutil.exe"
        && fa30.Contains("--ppid") && fa30.Contains("4242") && fa30.Contains("--tray"),
        "T30d handoff argv: staged exe replaces target, waits ppid exit, relaunches --tray (no CLI ever again)");
    Check(SelfUpdateWatch.StagePath("/d").Replace('\\', '/').EndsWith("/updates/esportsutil.exe.new"),
        "T30d staged exe lives under dataDir/updates; live path untouched until the handoff child runs");
    var suCfg = new EsportsConfig();
    Check(suCfg.SelfUpdateApply && suCfg.SelfUpdateCheckIntervalMin == 360
        && suCfg.GitHubRepo == "JonAmes/EsportUtil" && suCfg.TrayAutoStart,
        "T30e defaults: apply on, 6h cadence, venue repo, tray auto-load at logon");
    var dashSu = File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
        "EsportsUtil", "wwwroot", "dashboard.html")));
    Check(dashSu.Contains("/api/self-update/") && dashSu.Contains("suChip") && dashSu.Contains("appChips}${suChip}${jobs}"),
        "T30f dashboard ships the clickable app-update chip wired to the self_update directive");
}

// ---- T31: registry-complete app + game discovery ("have the app pull the info from the registry") ----
{
    var t31Views = StateModule.UninstallViewNames();
    Check(t31Views.Length == 4 && t31Views.Contains("HKLM|64") && t31Views.Contains("HKLM|32")
          && t31Views.Contains("HKCU|64") && t31Views.Contains("HKCU|32"),
        "T31a inventory spans all four Uninstall views — HKLM|32 is WOW6432Node, where Steam/Battle.net/Epic register; a 64-bit process never sees them in the default view");

    bool t31Exists(string p) => p.Replace('\\', '/').ToLowerInvariant() is var n
        && (n == "d:/steam/steamapps/common" || n == "e:/steamlib/steamapps/common");
    var t31Roots = GameScanner.SteamRootsFromRegistryValues("D:\\Steam", new[] { "d:\\steam", "E:\\SteamLib", "  ", "F:\\Nope" }, t31Exists);
    Check(t31Roots.Count == 2
          && t31Roots[0].Replace('\\', '/').ToLowerInvariant() == "d:/steam/steamapps/common"
          && t31Roots[1].Replace('\\', '/').ToLowerInvariant() == "e:/steamlib/steamapps/common",
        "T31b steam roots derived from registry InstallPath+SteamLibrary values: deduped case-insensitively, dead libraries dropped");

    Environment.SetEnvironmentVariable(GameScanner.FakeSteamRootsEnvVar, "/steam/reg|/games/extra");
    var t31Dirs = GameScanner.EffectiveScanDirs(new[] { "/x", "/steam/reg/" }, GameScanner.SteamScanRoots());
    Environment.SetEnvironmentVariable(GameScanner.FakeSteamRootsEnvVar, null);
    Check(t31Dirs.Count == 3 && t31Dirs[0] == "/x" && t31Dirs[1] == "/steam/reg/",
        "T31c ESU_FAKE_STEAM_ROOTS seam feeds the agent scan; trailing-slash dedupe, config order preserved");

    Environment.SetEnvironmentVariable(StateModule.FakeInstalledAppsEnvVar,
        """[{"display_name":"Battle.net","display_version":"1.2.3","hive":"HKLM","view":"32"},{"display_name":"Google Chrome","display_version":"157.0.0.0","hive":"HKCU","view":"64"}]""");
    var t31Inv = StateModule.InstalledAppsOrFake();
    var t31St = AppCatalog.Statuses(t31Inv, id => id == "battlenet" ? "1.2.3" : id == "chrome" ? "157.0.0.0" : null, 555);
    var t31Rec = StateModule.Record()["installedApps"]!.AsArray();
    Environment.SetEnvironmentVariable(StateModule.FakeInstalledAppsEnvVar, null);
    var t31Bn = t31St.First(s => s.AppId == "battlenet");
    var t31Ch = t31St.First(s => s.AppId == "chrome");
    Check(t31Inv.Count == 2 && t31Bn.Installed && t31Bn.Version == "1.2.3" && t31Bn.UpToDate == true
          && t31Ch.Installed && !t31St.First(s => s.AppId == "steam").Installed && t31Rec.Count == 2,
        "T31d fake-inventory seam drives the whole projection: Battle.net installed from an HKLM|32 (WOW6432Node) entry, per-user Chrome from HKCU|64, absent steam stays false");

    var t31Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    Check(File.ReadAllText(Path.Combine(t31Root, "schemas", "local_manifest.schema.json")).Contains("WOW6432Node")
          && File.ReadAllText(Path.Combine(t31Root, "README.md")).Contains("WOW6432Node")
          && File.ReadAllText(Path.Combine(t31Root, "deploy", "BUILD.md")).Contains("WOW6432Node"),
        "T31e docs+schema name the four registry views and the WOW6432Node trap once, so it is never re-discovered by an operator");

    var t32Dash = File.ReadAllText(Path.Combine(t31Root, "src", "EsportsUtil", "wwwroot", "dashboard.html"));
    Check(t32Dash.Contains("ia-modal") && t32Dash.Contains("openApps(") && t32Dash.Contains("installed_apps")
          && t32Dash.Contains("renderIApps()"),
        "T32b dashboard ships the installed-apps browser: 'N apps' chip opens a filterable modal fed by the projected installed_apps field");
}

// ---------- T33: one-click "install ALL Windows updates" per station ----------
// (user: "Updates button doesn't do anything. Lets have it run all updates for that unit.")
// Pure gating + cross-OS fake install seam first.
Check(!UpdateWatch.DueInstall(false, false, false) && UpdateWatch.DueInstall(false, false, true)
    && !UpdateWatch.DueInstall(false, true, true) && !UpdateWatch.DueInstall(true, false, true),
    "T33a DueInstall(): fires only with an install requested and no scan/install already in flight");
var t33Beats = new List<string>();
UpdateWatch.Reset();
Environment.SetEnvironmentVariable(UpdateWatch.FakeEnvVar, "3");
Environment.SetEnvironmentVariable(UpdateWatch.FakeInstallEnvVar, "20");
Environment.SetEnvironmentVariable(UpdateWatch.FakeRebootEnvVar, "1");
UpdateWatch.RequestInstall();
await UpdateWatch.InstallAsync(() => t33Beats.Add($"{UpdateWatch.Last!.InstallState}:{UpdateWatch.Last!.InstallDone}"));
Check(UpdateWatch.Last is { InstallState: "done", InstallDone: 3, InstallTotal: 3, RebootPending: true }
    && t33Beats.Contains("running:1") && t33Beats.Contains("running:2") && t33Beats.Count >= 4,
    "T33b fake install seam: running 1/3..3/3 stage beats then done 3/3 carrying the reboot verdict (fake WUA flag)");
var t33Ms = StateModule.Record();
Check(t33Ms["windowsUpdate"]?["install_state"]?.GetValue<string>() == "done"
    && t33Ms["windowsUpdate"]!["install_total"]?.GetValue<int>() == 3
    && t33Ms["windowsUpdate"]!["reboot_pending"]?.GetValue<bool>() == true,
    "T33c State Module embeds the install verdict into machine_state.windowsUpdate for the report");
// a timer scan landing mid-install must NOT erase the progress the dashboard is rendering
UpdateWatch.Reset();
Environment.SetEnvironmentVariable(UpdateWatch.FakeEnvVar, "2");
Environment.SetEnvironmentVariable(UpdateWatch.FakeInstallEnvVar, "300");
Environment.SetEnvironmentVariable(UpdateWatch.FakeRebootEnvVar, null);
var t33InstallTask = UpdateWatch.InstallAsync();
await Task.Delay(80);
await UpdateWatch.ScanAsync();
var t33Keep = UpdateWatch.Last!.InstallState == "running" && UpdateWatch.Last!.InstallTotal == 2;
await t33InstallTask;
Check(t33Keep && UpdateWatch.Last!.InstallState == "done" && !UpdateWatch.IsInstalling,
    "T33d MergeInstall: a scan during the install keeps running done/total visible; the run still finishes");
// update overlay: pure argv channel, no-dismiss contract, file-based progress
var t33Ov = new UpdateOverlayRequest("Jon MSI", "4821", "cid-t33", "Dell XPS 15", Path.Combine(tmp, "t33-progress.json"));
Check(UpdateOverlayRequest.TryParse(UpdateOverlayRequest.BuildArgs(t33Ov), out var t33Back) && t33Back == t33Ov
    && !t33Ov.DismissOnKey && !t33Ov.ShowBypassChip && !t33Ov.HasCountdown,
    "T33e update overlay argv round-trips to the same-EXE child; nothing on the machine dismisses it (agent owns its lifetime)");
UpdateOverlayRequest.WriteProgress(t33Ov.ProgressFile, "running", 2, 4, false);
var t33Prog = UpdateOverlayRequest.ReadProgress(t33Ov.ProgressFile);
Check(t33Prog is { State: "running", Done: 2, Total: 4 }
    && UpdateOverlayRequest.ProgressLine(t33Prog).Contains("2 of 4")
    && ConsoleOverlay.RenderUpdateCard(t33Ov).Contains("SYSTEM UPDATES"),
    "T33f progress channel: agent writes every stage, renderer re-reads each frame (never stdin/IPC)");
// live cycle: the T25 pair was stopped after T27, so re-start it — server registry AND the agent
// identity live on disk in their data dirs, so cid5 is still the same registered device.
using var app5b = ServerRole.BuildApp(srvCfg5); await app5b.StartAsync();
using var cts6 = new CancellationTokenSource();
var loop6 = new AgentLoop(new HttpClient { Timeout = TimeSpan.FromSeconds(15) }, agCfg5);
var loopTask6 = loop6.RunAsync(cts6.Token);
loop6.DownOverlaySurface = _ => new DownProbe();
loop6.UpdateOverlaySurface = _ => new DownProbe();
var t33Empty = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
var t33Nope = await probe5.PostAsync("/api/install-updates/nope", t33Empty);
Environment.SetEnvironmentVariable(UpdateWatch.FakeEnvVar, "4");
Environment.SetEnvironmentVariable(UpdateWatch.FakeInstallEnvVar, "25");
var t33Req = await probe5.PostAsync($"/api/install-updates/{cid5}", t33Empty);
Check((int)t33Nope.StatusCode == 404 && (int)t33Req.StatusCode == 200
    && await Until(async () => (await State5()).devices.First(x => x.client_id == cid5).desired_state == "downed", 10),
    "T33g /api/install-updates: unknown device 404; a live active station is auto-marked downed the moment the install is requested");
Check(await Until(async () => { var d = (await State5()).devices.First(x => x.client_id == cid5); return d.wu_install_state == "done" && d.wu_install_total == 4; }, 20),
    "T33h full cycle: install_updates directive → agent installs all 4 faked updates → done 4/4 streams to /api/state");
Check(await Until(async () => (await State5()).devices.First(x => x.client_id == cid5).desired_state == "active", 10),
    "T33i terminal install transition restores the auto-downed station to active (operator-owned downs would be untouched)");
var t33Alerts = (await State5()).alerts;
Check(t33Alerts.Any(a => a.type == "update_install_requested") && t33Alerts.Any(a => a.type == "update_install_done"),
    "T33j install request and completion raise operator alerts on the dashboard feed");
Environment.SetEnvironmentVariable(UpdateWatch.FakeEnvVar, null);
Environment.SetEnvironmentVariable(UpdateWatch.FakeInstallEnvVar, null);
UpdateWatch.Reset();
// dashboard + client view wiring
var t33Dash = await probe5.GetStringAsync("/");
Check(t33Dash.Contains("installUpdates(") && t33Dash.Contains("/api/install-updates/") && t33Dash.Contains("wu_install_state")
    && t33Dash.Contains("scanUpdates("),
    "T33k dashboard: 'updates' button POSTs install-updates (confirm-gated) and streams done/total; a separate re-scan keeps the poll-only path");
var t33Client = File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
    "EsportsUtil", "wwwroot", "client.html")));
Check(t33Client.Contains("install_state==='running'"),
    "T33l client view shows 'installing X/Y — do not power off' while a T33 install runs on the machine");
// ---------- T34: version visibility ----------
var t34Root = JsonNode.Parse(await probe5.GetStringAsync("/api/state"));
Check(t34Root["server_version"]!.GetValue<string>() == SelfUpdateWatch.CurrentVersion,
    "T34a /api/state projects server_version = the server build");
Check(t33Dash.Contains("id=\"c-ver\"") && t33Dash.Contains("dev.agent_version"),
    "T34b dashboard header carries the server version pill and each device row its agent build chip");
Check(t33Client.Contains("chipver"),
    "T34c client view shows the agent version chip in its header");
Check(AgentInfo.Version == SelfUpdateWatch.CurrentVersion && AgentInfo.Version != "0.1.0",
    "T34d agent reports/heartbeats carry the REAL build version, not the old 0.1.0 protocol constant");
cts6.Cancel(); await loopTask6; await app5b.StopAsync();

// ---------- T35: operator sign-in + LAN autodetection ----------
// Pure hashing: the stored recipe verifies, anything else does not, plaintext never appears.
var pwh = EsportsUtil.Server.PasswordHash.Create("hunter2!");
Check(EsportsUtil.Server.PasswordHash.Verify("hunter2!", pwh) &&
      !EsportsUtil.Server.PasswordHash.Verify("hunter3!", pwh) &&
      !EsportsUtil.Server.PasswordHash.Verify("hunter2!", "garbage") &&
      !EsportsUtil.Server.PasswordHash.Verify("", pwh) &&
      pwh.StartsWith("pbkdf2-sha256$100000$") && !pwh.Contains("hunter2"),
    "T35a PBKDF2 recipe verifies exactly; tampered/malformed input never verifies; no plaintext in the hash");
// Pure discovery helpers.
var dreply = EsportsUtil.Core.Discovery.ParseReply(
    "{\"service\":\"esportsutil\",\"server_url\":\"http://10.140.0.212:4300/\",\"version\":\"0.5.5\"}");
Check(dreply is { ServerUrl: "http://10.140.0.212:4300", Version: "0.5.5" } &&
      EsportsUtil.Core.Discovery.ParseReply("{\"service\":\"other\"}") is null &&
      EsportsUtil.Core.Discovery.ParseReply("not json") is null,
    "T35b discovery replies parse, foreign/malformed payloads are ignored");
Check(EsportsUtil.Core.Discovery.ResolveAdvertiseUrl("http://0.0.0.0:4300",
          System.Net.IPAddress.Parse("10.140.0.212"), 4300) == "http://10.140.0.212:4300" &&
      EsportsUtil.Core.Discovery.ResolveAdvertiseUrl("http://venue.tailnet.ts:4300",
          System.Net.IPAddress.Parse("10.140.0.212"), 4300) == "http://venue.tailnet.ts:4300",
    "T35c wildcard-bound server advertises the probe's own interface; explicit host advertises as-is");
Check(EsportsUtil.Core.Discovery.BroadcastTargets().Contains(System.Net.IPAddress.Broadcast),
    "T35d probe fan-out always includes the global broadcast");
// Login throttle: five strikes on a key lock it, a good login resets another key.
var guard = new EsportsUtil.Server.LoginGuard();
for (var i = 0; i < 5; i++) { guard.Fail("k"); }
Check(guard.Locked("k") && !guard.Locked("other"),
    "T35e five failed sign-ins lock that (ip,user) key only");
guard.Pass("k");
Check(!guard.Locked("k"), "T35f a successful sign-in clears the strike count");

// End-to-end on a FRESH server: open -> first operator arms the gate -> removal disarms it.
var t35Port = 4401; var t35Url = $"http://127.0.0.1:{t35Port}";
var t35Cfg = new EsportsConfig { Role = "server", ServerUrl = t35Url, DataDir = Path.Combine(tmp, "t35") };
using var t35App = ServerRole.BuildApp(t35Cfg); await t35App.StartAsync();
using var nc = new System.Net.Http.HttpClient(new System.Net.Http.HttpClientHandler { UseCookies = false, AllowAutoRedirect = false });
Check((await nc.GetAsync(t35Url + "/api/state")).StatusCode == System.Net.HttpStatusCode.OK &&
      (await nc.GetAsync(t35Url + "/")).StatusCode == System.Net.HttpStatusCode.OK,
    "T35g EMPTY operator store = fully open dashboard (live-server compatibility)");
Check(JsonNode.Parse(await nc.GetStringAsync(t35Url + "/api/auth-status"))["auth_required"]!.GetValue<bool>() == false,
    "T35h auth-status reports auth_required=false while no operator exists");
var addResp = await nc.PostAsJsonAsync(t35Url + "/api/operators", new { username = "Alice", password = "s3cret!" }, Wire.Options);
Check(addResp.StatusCode == System.Net.HttpStatusCode.OK, "T35i adding the first operator succeeds");
Check((await nc.GetAsync(t35Url + "/api/state")).StatusCode == System.Net.HttpStatusCode.Unauthorized &&
      (await nc.GetAsync(t35Url + "/")).StatusCode == System.Net.HttpStatusCode.Found,
    "T35j with an operator present, API calls 401 and pages redirect to /login");
Check((await nc.PostAsJsonAsync(t35Url + "/api/login", new { username = "alice", password = "nope" }, Wire.Options))
        .StatusCode == System.Net.HttpStatusCode.Unauthorized,
    "T35k wrong password is 401 (username is case-insensitive)");
var locked = false;
for (var i = 0; i < 6 && !locked; i++)   // strikes 1-4 free, strike 5 locks inside Fail
    locked = (await nc.PostAsJsonAsync(t35Url + "/api/login", new { username = "ghost", password = "x" + i }, Wire.Options))
        .StatusCode == System.Net.HttpStatusCode.TooManyRequests;
Check(locked, "T35l repeated failures on one key trip the 429 cool-off");
var okLogin = await nc.PostAsJsonAsync(t35Url + "/api/login", new { username = "alice", password = "s3cret!" }, Wire.Options);
var cookie = okLogin.Headers.TryGetValues("Set-Cookie", out var sc)
    ? sc.First(v => v.StartsWith("esu_session=")).Split(';')[0] : null;
Check(okLogin.StatusCode == System.Net.HttpStatusCode.OK && cookie is not null, "T35m correct login sets the session cookie");
nc.DefaultRequestHeaders.Add("Cookie", cookie!);
Check((await nc.GetAsync(t35Url + "/api/state")).StatusCode == System.Net.HttpStatusCode.OK &&
      JsonNode.Parse(await nc.GetStringAsync(t35Url + "/api/auth-status"))["signed_in"]!.GetValue<bool>() &&
      JsonNode.Parse(await nc.GetStringAsync(t35Url + "/api/auth-status"))["username"]!.GetValue<string>() == "alice",
    "T35n the session cookie opens the API and auth-status names the operator");
Check((await nc.PostAsJsonAsync(t35Url + "/api/operators", new { username = "alice", password = "dup" }, Wire.Options))
        .StatusCode == System.Net.HttpStatusCode.BadRequest,
    "T35o duplicate usernames are rejected");
var seedDir = Path.Combine(tmp, "t35seed");
var seedCfg = new EsportsConfig { Role = "server", ServerUrl = "http://127.0.0.1:4403", DataDir = seedDir, DiscoveryPort = 0 };
seedCfg.BootstrapOperator = new OperatorSeed { Username = "seedop", Password = "seedpass" };
Check(!File.Exists(Path.Combine(seedDir, "operators.json")), "T35p no operators.json before the server ever starts");
using var seedApp = ServerRole.BuildApp(seedCfg); await seedApp.StartAsync();
Check(File.Exists(Path.Combine(seedDir, "operators.json")) &&
      (await nc.PostAsJsonAsync(t35Url + "/api/login", new { username = "n/a", password = "n/a" }, Wire.Options)).StatusCode
          == System.Net.HttpStatusCode.Unauthorized,
    "T35q bootstrap_operator seeds operators.json exactly once at startup");
Check(!File.ReadAllText(Path.Combine(seedDir, "operators.json")).Contains("seedpass"),
    "T35r the seed password NEVER appears in operators.json (only the PBKDF2 recipe)");
var seedLogin = await new System.Net.Http.HttpClient().PostAsJsonAsync("http://127.0.0.1:4403/api/login",
    new { username = "seedop", password = "seedpass" }, Wire.Options);
Check(seedLogin.StatusCode == System.Net.HttpStatusCode.OK, "T35s the seeded operator can sign in");
await seedApp.StopAsync();
seedCfg.BootstrapOperator = new OperatorSeed { Username = "seedop", Password = "changed!" };
using var seedApp2 = ServerRole.BuildApp(seedCfg); await seedApp2.StartAsync();
Check((await new System.Net.Http.HttpClient().PostAsJsonAsync("http://127.0.0.1:4403/api/login",
        new { username = "seedop", password = "changed!" }, Wire.Options)).StatusCode
        == System.Net.HttpStatusCode.Unauthorized,
    "T35t a changed bootstrap_operator never re-seeds an existing store");
await seedApp2.StopAsync();
// LAN autodetection end-to-end: UDP probe in, reachable URL + build out.
var discPort = 4402;
var discCfg = new EsportsConfig { Role = "server", ServerUrl = "http://127.0.0.1:4403", DataDir = Path.Combine(tmp, "t35disc"), DiscoveryPort = discPort };
using var discApp = ServerRole.BuildApp(discCfg); await discApp.StartAsync();
using (var udp = new System.Net.Sockets.UdpClient())
{
    await udp.SendAsync(EsportsUtil.Core.Discovery.Probe, EsportsUtil.Core.Discovery.Probe.Length,
        new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, discPort));
    udp.Client.ReceiveTimeout = 3000;
    var got = await udp.ReceiveAsync();
    var drep = EsportsUtil.Core.Discovery.ParseReply(System.Text.Encoding.UTF8.GetString(got.Buffer));
    Check(drep is { ServerUrl: "http://127.0.0.1:4403" } && drep.Version == SelfUpdateWatch.CurrentVersion,
        "T35u discovery responder answers the probe with the reachable URL and real build");
}
Check(new EsportsConfig().DiscoveryPort == EsportsUtil.Core.Discovery.DefaultPort &&
      new EsportsConfig { DiscoveryPort = 0 }.DiscoveryPort == 0,
    "T35v discovery_port defaults to 4301 and 0 disables (seed server above ran with 0)");
// UI seams (files on disk, same pattern as the T33 client check).
string WwwRoot(string f) => File.ReadAllText(Path.GetFullPath(Path.Combine(
    AppContext.BaseDirectory, "..", "..", "..", "..", "EsportsUtil", "wwwroot", f)));
var t35Login = WwwRoot("login.html");
var t35Dash = WwwRoot("dashboard.html");
var t35Set = WwwRoot("settings.html");
Check(t35Login.Contains("/api/login") && t35Dash.Contains("/login") && t35Dash.Contains("c-user") &&
      t35Dash.Contains("status === 401"),
    "T35w sign-in page exists; dashboard redirects on 401 and shows the operator chip");
Check(t35Set.Contains("/api/operators") && t35Set.Contains("add operator"),
    "T35x settings carries the operator management panel");

// ---------- T36: factory-state pull + compliance dismissals ----------
{
    var port8 = 4391; var srvUrl8 = $"http://127.0.0.1:{port8}"; var srvDir8 = Path.Combine(tmp, "srv8");
    var srvCfg8 = new EsportsConfig { Role = "server", ServerUrl = $"http://0.0.0.0:{port8}", DataDir = srvDir8 };
    using var app8 = ServerRole.BuildApp(srvCfg8); await app8.StartAsync();
    using var probe8 = new HttpClient { BaseAddress = new Uri(srvUrl8), Timeout = TimeSpan.FromSeconds(5) };
    async Task<(int code, JsonNode json)> Api8(string path, object? body = null)
    {
        var r = await probe8.PostAsync(path, new StringContent(Wire.ToJson(body ?? new { }), System.Text.Encoding.UTF8, "application/json"));
        var txt = await r.Content.ReadAsStringAsync();
        return ((int)r.StatusCode, txt.Length > 0 ? JsonNode.Parse(txt)! : JsonNode.Parse("null")!);
    }
    async Task<JsonDoc> State8() => Wire.FromJson<JsonDoc>(await probe8.GetStringAsync("/api/state"))!;

    var cid8 = "t36-" + Guid.NewGuid().ToString("N")[..8];
    Check((await probe8.PostAsync("/api/register", new StringContent(Wire.ToJson(new RegisterRequest(cid8, "9911", "Vertex Prime X")), System.Text.Encoding.UTF8, "application/json"))).IsSuccessStatusCode,
        "T36a station registers for the factory/compliance harness");
    await Api8("/api/rename", new RenameRequest(cid8, "LAN-TEST-01", "Zone T"));
    File.WriteAllText(Path.Combine(srvDir8, "ApprovedSoftwarePolicy.json"), Wire.ToJson(new ApprovedSoftwarePolicy(
        1, DateTimeOffset.UtcNow, "report_only",
        new List<SoftwareRule> { new("Steam") }, new List<SoftwareRule>())));
    var ms8 = JsonNode.Parse("""
        {"machineModel":"Vertex Prime X","installedApps":[
          {"display_name":"Steam","publisher":"Valve"},
          {"display_name":"Notepad++","publisher":"Notepad++ Team"},
          {"display_name":"Discord","publisher":"Discord Inc."}]}
        """)!;
    Check((await Api8("/api/report", new LocalManifest(cid8, "9911", "LAN-TEST-01", DateTimeOffset.UtcNow,
        AgentInfo.Version, ms8, new List<GameReport>()))).code == 200,
        "T36b seeded full report with installedApps accepted");

    var dev8 = (await State8()).devices.Single(x => x.client_id == cid8);
    Check(dev8.issues.Count(i => i.kind == "unapproved_app") == 2 && (dev8.cleared_issues ?? 0) == 0,
        "T36c policy flags Notepad++ + Discord; nothing cleared yet");

    var (cc, cj) = await Api8("/api/issues/clear", new IssueActionRequest(cid8));
    dev8 = (await State8()).devices.Single(x => x.client_id == cid8);
    Check(cc == 200 && cj["ok"]!.GetValue<bool>() && cj["cleared"]!.GetValue<int>() == 2 &&
          dev8.issues.Count == 0 && dev8.cleared_issues == 2,
        "T36d clear silences every current finding; issues empty, cleared_issues rides to the UI");
    Check(File.Exists(Path.Combine(srvDir8, "issue-dismissals.json")) &&
          new EsportsUtil.Server.DismissalsStore(srvDir8).CountFor(cid8) == 2,
        "T36e dismissals persist to <data>/issue-dismissals.json and reload on a fresh store");

    var comps = (await State8()).alerts.Count(a => a.type == "compliance");
    await Api8("/api/issues/clear", new IssueActionRequest(cid8));   // no-op re-clear
    Check(comps == 1 && (await State8()).alerts.Count(a => a.type == "compliance") == 1 &&
          (await State8()).alerts.Any(a => a.type == "compliance_cleared"),
        "T36f compliance alert fired ONCE for the findings and re-clearing never re-alerts");

    var (rcj, rjj) = await Api8("/api/issues/restore", new IssueActionRequest(cid8));
    dev8 = (await State8()).devices.Single(x => x.client_id == cid8);
    Check(rcj == 200 && rjj["restored"]!.GetValue<int>() == 2 &&
          dev8.issues.Count(i => i.kind == "unapproved_app") == 2 && (dev8.cleared_issues ?? 0) == 0,
        "T36g restore lifts the dismissals and the still-true findings speak again");

    var (fc, fj) = await Api8("/api/factory-state/" + cid8);
    var fentries = JsonNode.Parse(await probe8.GetStringAsync("/api/factory-state"))!.AsArray();
    var fent = fentries.Single();
    Check(fc == 200 && fj["ok"]!.GetValue<bool>() && fj["installed_apps"]!.GetValue<int>() == 3 &&
          fentries.Count == 1 &&
          fent["display_name"]!.GetValue<string>() == "LAN-TEST-01" &&
          fent["entry_id"]!.GetValue<string>() == cid8 &&
          fent["source_client_id"]!.GetValue<string>() == cid8 &&
          fent["machine_model"]!.GetValue<string>() == "Vertex Prime X" &&
          fent["installed_apps"]!.AsArray().Select(x => x!.GetValue<string>()).OrderBy(s => s)
              .SequenceEqual(new[] { "Discord", "Notepad++", "Steam" }) &&
          fent["installed_apps"]!.AsArray().All(x => x!.GetValueKind() == JsonValueKind.String),
        "T36h factory pull saves NAME + every installed app NAME (strings only — no version numbers anywhere)");
    await Api8("/api/factory-state/" + cid8);
    Check(JsonNode.Parse(await probe8.GetStringAsync("/api/factory-state"))!.AsArray().Count == 1,
        "T36i re-pull REPLACES the entry — no duplicate factory baselines");

    var t36no = "t36noreport-" + Guid.NewGuid().ToString("N")[..8];
    await probe8.PostAsync("/api/register", new StringContent(Wire.ToJson(new RegisterRequest(t36no, "9912", "Ghost")), System.Text.Encoding.UTF8, "application/json"));
    Check((await Api8("/api/factory-state/" + t36no)).code == 404 &&
          (await Api8("/api/factory-state/nope")).code == 404 &&
          (await Api8("/api/issues/clear", new IssueActionRequest("nope"))).code == 404,
        "T36j factory pull of a never-reported device 404s with a clear message; unknown clients 404 everywhere");

    await app8.StopAsync();
}
var t36Dash = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "wwwroot", "dashboard.html"));
Check(t36Dash.Contains("id=\"c-compliance\"") && t36Dash.Contains("gotoCompliance()") &&
      t36Dash.Contains("getElementById('fleet').scrollIntoView") && !t36Dash.Contains("s-warn\">\n      <div class=\"l\"") &&
      t36Dash.Contains("clearIssues(") && t36Dash.Contains("restoreIssues(") && t36Dash.Contains("cleared ↺") &&
      t36Dash.Contains("saveFactory("),
    "T36k dashboard ships ONE orange compliance pill that scrolls to the fleet, clear/restore row actions and a factory button — old warn stat tile is gone");
var t36Schema = File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "schemas", "master_manifest.schema.json")));
Check(t36Schema.Contains("\"installed_apps\"") && t36Schema.Contains("names only, NO versions"),
    "T36l factory-entry schema carries display_name + installed_apps and states versions are out of scope");


// ---------- T37: deterministic identity — updates/moves never mint a new device ----------
{
    var prevSeed = Environment.GetEnvironmentVariable("ESU_HW_SEED");
    try
    {
        Environment.SetEnvironmentVariable("ESU_HW_SEED", "t37-box-A");
        var dir1 = Path.Combine(tmp, "t37-dir1");
        var dir2 = Path.Combine(tmp, "t37-dir2");           // EXE+config MOVED elsewhere: fresh empty data dir
        var m1 = Identity.LoadOrCreate(dir1);
        var m2 = Identity.LoadOrCreate(dir2);
        Check(Guid.TryParse(m1.ClientId, out _) && m1.ClientId == m2.ClientId && m1.DevicePin == m2.DevicePin,
            "T37a lost identity dir re-mints the SAME client_id+PIN on the same machine — update/move invisible to the server");
        Check(m1.ClientId == Identity.DeterministicId(Environment.MachineName + "|t37-box-A"),
            "T37b minted client_id is SHA-derived from machine name + hardware seed");
        Environment.SetEnvironmentVariable("ESU_HW_SEED", "t37-box-B");
        var m3 = Identity.LoadOrCreate(Path.Combine(tmp, "t37-dir3"));
        Check(m3.ClientId != m1.ClientId, "T37c a different machine still gets its own id (no cross-station collision)");
        Environment.SetEnvironmentVariable("ESU_HW_SEED", "t37-box-A-seed-drift");
        var m4 = Identity.LoadOrCreate(dir1);
        Check(m4.ClientId == m1.ClientId && m4.DevicePin == m1.DevicePin,
            "T37d persisted identity always wins, even if the hardware seed later drifts (WMI flake)");
        var rot1 = Identity.Rotate(dir1);
        Check(rot1.ClientId != m1.ClientId && Identity.LoadOrCreate(dir1).ClientId == rot1.ClientId,
            "T37e offboard Rotate burns the id (salt advance) and the rotated id persists");
        var rot2 = Identity.Rotate(dir1);
        Check(rot2.ClientId != rot1.ClientId && rot2.DevicePin == rot1.DevicePin,
            "T37f re-rotation keeps advancing — a tombstone can never pin a machine forever; PIN badge kept");
        Environment.SetEnvironmentVariable("ESU_HW_SEED", "t37-box-A");
        var cfgDir = Path.Combine(tmp, "t37-cfg");
        Directory.CreateDirectory(cfgDir);
        var cfgPath = Path.Combine(cfgDir, "esportsutil.config.json");
        File.WriteAllText(cfgPath, Wire.ToJson(new EsportsConfig { Role = "agent", DataDir = "mydata" }));
        var c9 = EsportsConfig.Load(cfgPath);
        c9.ResolveDataDir(cfgPath);
        Check(c9.DataDir == Path.Combine(cfgDir, "mydata"),
            "T37g relative data_dir binds to the config FILE, never the launch CWD");
        Check(Identity.LoadOrCreate(c9.DataDir).ClientId == m1.ClientId,
            "T37h the config-bound data dir carries the same machine identity");

        // ---------- T38: per-station findings collapse into ONE pill (user: "Lets collaps all the game pills into one pill") ----------
        var dash9 = File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "EsportsUtil", "wwwroot", "dashboard.html")));
        Check(dash9.Contains("function toggleFindings(") && dash9.Contains("_fxOpen")
            && dash9.Contains("finding${issues.length===1?'':'s'}") && dash9.Contains("id=\"fx-${dev.client_id}\""),
            "T38a one summary findings pill per station (count + caret); detail chips sit in a hidden expandable box");
        Check(dash9.Contains("onclick=\"fixJob('${dev.client_id}'") && dash9.Contains("onclick=\"clearIssues('${dev.client_id}')\"")
            && dash9.IndexOf("onclick=\"clearIssues") > dash9.IndexOf("id=\"fx-${dev.client_id}\""),
            "T38b collapse loses nothing: fixJob actions survive inside the box; clear/restore stay outside it");
        Check(dash9.Contains("_fxOpen.add(cid)") && dash9.Contains("scrollIntoView({behavior:'smooth'"),
            "T38c header compliance pill expands every affected station, then jumps to #fleet");
    }
    finally { Environment.SetEnvironmentVariable("ESU_HW_SEED", prevSeed); }
}

// ---------- T39: factory DEPLOY — push a baseline ONTO a machine ----------
{
    var port9 = 4393; var url9 = $"http://127.0.0.1:{port9}"; var dir9 = Path.Combine(tmp, "srv9");
    var cfg9 = new EsportsConfig { Role = "server", ServerUrl = $"http://0.0.0.0:{port9}", DataDir = dir9 };
    using var app9 = ServerRole.BuildApp(cfg9); await app9.StartAsync();
    using var probe9 = new HttpClient { BaseAddress = new Uri(url9), Timeout = TimeSpan.FromSeconds(5) };
    async Task<(int code, JsonNode json)> Api9(string path, object? body = null)
    {
        var r = await probe9.PostAsync(path, new StringContent(Wire.ToJson(body ?? new { }), System.Text.Encoding.UTF8, "application/json"));
        var txt = await r.Content.ReadAsStringAsync();
        return ((int)r.StatusCode, txt.Length > 0 ? JsonNode.Parse(txt)! : JsonNode.Parse("null")!);
    }
    async Task<JsonNode> Get9(string path) => JsonNode.Parse(await probe9.GetStringAsync(path))!;
    async Task Reg9(string cid, string pin, string model) =>
        await probe9.PostAsync("/api/register", new StringContent(Wire.ToJson(new RegisterRequest(cid, pin, model)), System.Text.Encoding.UTF8, "application/json"));

    File.WriteAllText(Path.Combine(dir9, "ApprovedSoftwarePolicy.json"), Wire.ToJson(new ApprovedSoftwarePolicy(
        1, DateTimeOffset.UtcNow, "report_only",
        new List<SoftwareRule> { new("Steam"), new("Discord") },
        new List<SoftwareRule> { new("Rogue Suite") })));

    var src9 = "t39src-" + Guid.NewGuid().ToString("N")[..8];
    var tgt9 = "t39tgt-" + Guid.NewGuid().ToString("N")[..8];
    var nor9 = "t39nor-" + Guid.NewGuid().ToString("N")[..8];
    await Reg9(src9, "9931", "Vertex Prime X"); await Reg9(tgt9, "9932", "Vertex Prime X"); await Reg9(nor9, "9933", "Ghost");
    await Api9("/api/rename", new RenameRequest(src9, "Prime Template", "Zone A"));
    await Api9("/api/rename", new RenameRequest(tgt9, "Messy Station", "Zone B"));

    var srcMs9 = JsonNode.Parse("""
        {"machineModel":"Vertex Prime X","installedApps":[
          {"display_name":"Steam","publisher":"Valve"},
          {"display_name":"WidgetPro Key","publisher":"WidgetPro"}]}
        """)!;
    var tgtMs9 = JsonNode.Parse("""
        {"machineModel":"Vertex Prime X","installedApps":[
          {"display_name":"Discord","publisher":"Discord Inc."},
          {"display_name":"Leftovers Game","publisher":"Nobody"},
          {"display_name":"Rogue Suite","publisher":"Rogue"},
          {"display_name":"EsportsUtil Agent","publisher":"EsportsUtil"}]}
        """)!;
    Check((await Api9("/api/report", new LocalManifest(src9, "9931", "Prime Template", DateTimeOffset.UtcNow, AgentInfo.Version, srcMs9, new List<GameReport>()))).code == 200,
        "T39a template source seeded its full report");
    Check((await Api9("/api/report", new LocalManifest(tgt9, "9932", "Messy Station", DateTimeOffset.UtcNow, AgentInfo.Version, tgtMs9, new List<GameReport>()))).code == 200,
        "T39b messy target seeded its full report");
    Check((await Api9("/api/factory-state/" + src9)).json["ok"]!.GetValue<bool>(),
        "T39c source baseline pulled (Steam + WidgetPro Key names, no versions)");

    var pv9 = await Get9($"/api/factory-deploy-preview/{tgt9}?source={src9}");
    Check(pv9["installs"]!.AsArray().Select(x => x!.GetValue<string>()).SequenceEqual(new[] { "Steam" }) &&
          pv9["not_auto_installable"]!.AsArray().Select(x => x!.GetValue<string>()).SequenceEqual(new[] { "WidgetPro Key" }) &&
          pv9["removals"]!.AsArray().Select(x => x!.GetValue<string>()).OrderBy(s => s).SequenceEqual(new[] { "Discord", "Leftovers Game" }),
        "T39d preview: Steam installs from the catalog, WidgetPro Key reported not-auto-installable, Discord+Leftovers slated for uninstall");
    Check(!pv9["removals"]!.AsArray().Any(x => x!.GetValue<string>() is "Rogue Suite" or "EsportsUtil Agent"),
        "T39e true factory reset still spares policy-PROTECTED entries and the agent itself");

    var (dc9, dj9) = await Api9("/api/factory-deploy/" + tgt9, new FactoryDeployRequest(src9));
    var dev9 = Wire.FromJson<JsonDoc>(await probe9.GetStringAsync("/api/state"))!.devices.Single(x => x.client_id == tgt9);
    Check(dc9 == 200 && dj9["queued_jobs"]!.AsArray().Count == 3 && dj9["deduped"]!.GetValue<int>() == 0 &&
          dj9["source"]!.GetValue<string>() == src9,
        "T39f deploy queued exactly 3 jobs (1 install + 2 removals) from the source baseline");
    Check((dev9.jobs ?? new()).Any(j => j.kind == "install_app" && j.subject == "steam") &&
          (dev9.jobs ?? new()).Count(j => j.kind == "remove_software") == 2,
        "T39g the jobs ride to the target as install_app steam + two remove_software jobs the agent already knows how to run");

    var (dr9, djr9) = await Api9("/api/factory-deploy/" + tgt9, new FactoryDeployRequest(src9));
    Check(dr9 == 200 && djr9["queued_jobs"]!.AsArray().Count == 0 && djr9["deduped"]!.GetValue<int>() == 3,
        "T39h re-deploy is a no-op — open jobs dedup, no double installs/uninstalls");

    Check((await Api9("/api/factory-deploy/" + nor9, new FactoryDeployRequest(src9))).code == 404 &&
          (await Api9("/api/factory-deploy/" + tgt9, new FactoryDeployRequest("bogus-source"))).code == 404 &&
          (await Api9("/api/factory-deploy/nope", new FactoryDeployRequest(src9))).code == 404,
        "T39i deploy 404s for a no-report target, a bogus baseline source, and an unknown device");

    await Api9("/api/factory-state/" + tgt9);   // target captures its own baseline
    var pvSelf = await Get9($"/api/factory-deploy-preview/{tgt9}");
    Check(pvSelf["source"]!.GetValue<string>() == tgt9 &&
          pvSelf["installs"]!.AsArray().Count == 0 && pvSelf["removals"]!.AsArray().Count == 0,
        "T39j self-reconcile: preview with no source diffs the target against ITS OWN baseline — a clean no-op");

    var dash10 = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "wwwroot", "dashboard.html"));
    Check(dash10.Contains("deployFactory(") && dash10.Contains("id=\"dep-modal\"") &&
          dash10.Contains("factory-deploy-preview") && dash10.Contains("depGo()") &&
          dash10.Contains("d.factory_entries ?") && dash10.Contains("Factory reset will UNINSTALL"),
        "T39k dashboard ships a per-row deploy button (only once a baseline exists), a preview modal, and a destructive-removals second confirm");

    // ---------- T40: the header manifest chip was dead text — give it the manifest itself ----------
    var man40 = await Get9("/api/manifest");
    Check(man40["version"]!.GetValue<int>() >= 1 && man40["updated_timestamp"]!.GetValue<string>().Length > 10 &&
          man40["master_list"] is not null && man40["factory_state_map"]!.AsArray().Count == 2,
        "T40a GET /api/manifest serves the live master manifest — version, updated stamp, game list, both factory baselines");
    Check(dash10.Contains("onclick=\"showManifest()\"") && dash10.Contains("id=\"mf-modal\"") &&
          dash10.Contains("fetch('/api/manifest')") && dash10.Contains("managed games"),
        "T40b the manifest chip is clickable and opens a real browse modal (games + baselines + stamp) — no more pill that does nothing");

    // ---------- T41: master-list add by Steam AppID — steamcmd resolves, records unique entries ----------
    var unit41 = SteamInfo.Parse("""
"730"
(
		"name"		"Counter-Strike 2"
		"depots"
		(
			"731"
			(
				"buildid"		"1111"
			)
			"730"
			(
				"buildid"		"2222"
			)
		)
)
""", "730");
    Check(unit41 is { Name: "Counter-Strike 2", BuildId: "2222" },
        "T41a VDF parse takes the app name + the MAIN depot buildid (depot key == AppID), not the first depot and not an awk field slot");
    var fake41 = Path.Combine(Path.GetTempPath(), $"esu_fake_steam_{Guid.NewGuid():N}.sh");
    File.WriteAllText(fake41, """
#!/bin/sh
printf '"730"\n(\n\t\t"name"\t\t"Counter-Strike 2"\n\t\t"depots"\n\t\t(\n\t\t\t"730"\n\t\t\t(\n\t\t\t\t"buildid"\t\t"987654"\n\t\t\t)\n\t\t)\n)\n'
""");
    Environment.SetEnvironmentVariable("ESU_FAKE_STEAMCMD", fake41);
    File.SetUnixFileMode(fake41, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    var add41 = await Api9("/api/master-add", new MasterAddRequest("730"));
    Check(add41.code == 200 && add41.json["game"]!.GetValue<string>() == "Counter-Strike 2" &&
          add41.json["app_id"]!.GetValue<string>() == "730" && add41.json["build_id"]!.GetValue<string>() == "987654",
        "T41b POST /api/master-add runs the steamcmd lookup and returns the resolved game + AppID + BuildID");
    var mf41 = await Get9("/api/manifest");
    var ml41 = mf41["master_list"]!.AsArray();
    Check(ml41.Count == 1 && ml41[0]!["app_id"]!.GetValue<string>() == "730" &&
          ml41[0]!["game_id"]!.GetValue<string>() == "counter_strike_2" &&
          ml41[0]!["display_name"]!.GetValue<string>() == "Counter-Strike 2" &&
          ml41[0]!["latest_version"]!.GetValue<string>() == "987654" &&
          ml41[0]!["launcher"]!.GetValue<string>() == "steam" &&
          mf41["version"]!.GetValue<int>() == man40["version"]!.GetValue<int>() + 1,
        "T41c the recorded entry pairs AppID with the resolved game (build id as version) and bumps the manifest version");
    var dup41 = await Api9("/api/master-add", new MasterAddRequest("730"));
    Check(dup41.code == 409 && (await Get9("/api/manifest"))["master_list"]!.AsArray().Count == 1,
        "T41d re-adding a known AppID is rejected with a clear message — the list stays unique");
    File.WriteAllText(fake41, """
#!/bin/sh
printf '"999"\n(\n\t\t"name"\t\t"Counter-Strike 2"\n\t\t"depots"\n\t\t(\n\t\t\t"999"\n\t\t\t(\n\t\t\t\t"buildid"\t\t"444"\n\t\t\t)\n\t\t)\n)\n'
""");
    Check((await Api9("/api/master-add", new MasterAddRequest("999"))).code == 409,
        "T41e a DIFFERENT AppID resolving to an already-listed game name is also rejected — unique entries by identity, not just literal id");
    Check((await Api9("/api/master-add", new MasterAddRequest("7x"))).code == 400,
        "T41f a non-numeric AppID is rejected before anything runs (it also never reaches a shell)");
    File.WriteAllText(fake41, "#!/bin/sh\nprintf 'Redirecting stderr...\\nNo product for app 555\\n'\n");
    Check((await Api9("/api/master-add", new MasterAddRequest("555"))).code == 502 &&
          (await Get9("/api/manifest"))["master_list"]!.AsArray().Count == 1,
        "T41g an unresolvable AppID returns 502 with the steamcmd reason — never a guessed master entry");
    Environment.SetEnvironmentVariable("ESU_FAKE_STEAMCMD", null);
    File.Delete(fake41);
    Check(dash10.IndexOf("id=\"ma-box\"") > dash10.IndexOf("id=\"fleet\"") &&
          dash10.Contains("addGameByApp(") && dash10.Contains("/api/master-add") &&
          dash10.Contains("duplicates are rejected") && dash10.Contains("id=\"ma-list\""),
        "T41h the add-by-AppID box sits UNDER the fleet view, posts to master-add, and lists the recorded entries");

    // ---------- T42: master-entry verbs — install / push / uninstall / remove ----------
    Check(GameScanner.NormalizeFolder("Counter-Strike 2") == "counter_strike_2",
        "T42a the fleet pairing id is the folder-normalized name — master entries and agent reports speak the same language");

    var dir9b = Path.Combine(tmp, "srv9b"); Directory.CreateDirectory(dir9b);
    File.WriteAllText(Path.Combine(dir9b, "MasterManifest.json"), Wire.ToJson(new MasterManifest(7, DateTimeOffset.UtcNow,
        new List<MasterGame> { new("Counter-Strike 2", "999", Launcher: "steam", AppId: "730") }, new List<FactoryEntry>())));
    using var app9b = ServerRole.BuildApp(new EsportsConfig { Role = "server", ServerUrl = "http://0.0.0.0:4395", DataDir = dir9b });
    await app9b.StartAsync();
    using var probe9b = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:4395"), Timeout = TimeSpan.FromSeconds(5) };
    var mig42 = JsonNode.Parse(await probe9b.GetStringAsync("/api/manifest"))!;
    Check(mig42["master_list"]![0]!["game_id"]!.GetValue<string>() == "counter_strike_2" &&
          mig42["master_list"]![0]!["display_name"]!.GetValue<string>() == "Counter-Strike 2" &&
          mig42["version"]!.GetValue<int>() == 8,
        "T42b a legacy display-name master entry is migrated to the fleet id exactly once at startup (version bumped once, name kept)");
    var npReg9b = await probe9b.PostAsync("/api/register", new StringContent(Wire.ToJson(new RegisterRequest("t42np", "9941", "Vertex Prime X")), System.Text.Encoding.UTF8, "application/json"));
    var np42 = await probe9b.PostAsync("/api/master-install", new StringContent(Wire.ToJson(new MasterJobRequest("730")), System.Text.Encoding.UTF8, "application/json"));
    var npJson42 = JsonNode.Parse(await np42.Content.ReadAsStringAsync())!;
    Check(npReg9b.IsSuccessStatusCode && (int)np42.StatusCode == 200 && npJson42["queued"]!.GetValue<int>() == 1,
        "T42l master verbs queue with NO policy file present — a null LoadPolicy must never 500 the queue (live v0.5.12 regression)");
    await app9b.StopAsync();

    var mi42 = await Api9("/api/master-install", new MasterJobRequest("730", tgt9));
    var devT42 = Wire.FromJson<JsonDoc>(await probe9.GetStringAsync("/api/state"))!.devices.Single(x => x.client_id == tgt9);
    Check(mi42.code == 200 && mi42.json["queued"]!.GetValue<int>() == 1 &&
          (devT42.jobs ?? new()).Any(j => j.kind == "update_game" && j.subject == "counter_strike_2"),
        "T42c 'install' queues ONE update_game job on the chosen station — steamcmd app_update installs when absent");
    var mi42b = await Api9("/api/master-install", new MasterJobRequest("730", tgt9));
    Check(mi42b.code == 200 && mi42b.json["queued"]!.GetValue<int>() == 0 && mi42b.json["skipped"]!.GetValue<int>() == 1,
        "T42d re-install dedups against the open job — no double installs");
    var mp42 = await Api9("/api/master-install", new MasterJobRequest("730"));   // push = no client_id
    Check(mp42.code == 200 && mp42.json["queued"]!.GetValue<int>() == 2 && mp42.json["skipped"]!.GetValue<int>() == 1,
        "T42e 'push' (client_id omitted) fans the install out to EVERY active station; the already-queued one dedups");
    var mu42 = await Api9("/api/master-uninstall", new MasterJobRequest("730"));
    var st42 = Wire.FromJson<JsonDoc>(await probe9.GetStringAsync("/api/state"))!;
    Check(mu42.code == 200 && mu42.json["queued"]!.GetValue<int>() == 3 &&
          st42.devices.Count(d => (d.jobs ?? new()).Any(j => j.kind == "remove_game" && j.subject == "counter_strike_2")) == 3,
        "T42f 'uninstall' rides to every station as a remove_game job of the SAME fleet id the agent scans for");
    Check((await Api9("/api/master-install", new MasterJobRequest("12345", tgt9))).code == 404 &&
          (await Api9("/api/master-uninstall", new MasterJobRequest("730", "nope"))).code == 404,
        "T42g unknown master entry and unknown client both 404 — jobs never aim at nothing");

    File.WriteAllText(Path.Combine(dir9, "ApprovedSoftwarePolicy.json"), Wire.ToJson(new ApprovedSoftwarePolicy(
        2, DateTimeOffset.UtcNow, "report_only",
        new List<SoftwareRule> { new("Steam"), new("Discord") },
        new List<SoftwareRule> { new("Rogue Suite"), new("Counter-Strike 2") })));
    var mv42 = await Api9("/api/master-install", new MasterJobRequest("730"));
    Check(mv42.code == 200 && mv42.json["queued"]!.GetValue<int>() == 0 && mv42.json["skipped"]!.GetValue<int>() == 3 &&
          mv42.json["reasons"]!.AsArray().Any(x => x!.GetValue<string>().Contains("vetoed")),
        "T42h a policy-PROTECTED game is vetoed by protected policy rules before ANY queueing — install and uninstall both");

    var mrBad42 = await Api9("/api/master-remove", new MasterRemoveRequest("9999"));
    var verBefore42 = (await Get9("/api/manifest"))["version"]!.GetValue<int>();
    var mr42 = await Api9("/api/master-remove", new MasterRemoveRequest("730"));
    var mf42 = await Get9("/api/manifest");
    Check(mrBad42.code == 404 && mr42.code == 200 && mf42["master_list"]!.AsArray().Count == 0 &&
          mf42["version"]!.GetValue<int>() == verBefore42 + 1 &&
          (await Api9("/api/master-install", new MasterJobRequest("730", tgt9))).code == 404,
        "T42i 'remove' deletes the MASTER-LIST entry only (manifest bumps, installed copies untouched); gone entries cannot queue jobs");

    var rg42 = JobRunner.RemoveGamePlan(new[]
    {
        new GameReport("counter_strike_2", "Counter-Strike 2", "/steam/common/cs2", "1", "steam", "OK"),
        new GameReport("counter_strike_2", "Counter-Strike 2", "/extra/CS2", "1", "steam", "DUPLICATE_FOUND"),
        new GameReport("dota_2", "Dota 2", "/steam/common/dota 2", "1", "steam", "OK"),
    }, "COUNTER_STRIKE_2");
    Check(rg42.SequenceEqual(new[] { "/steam/common/cs2", "/extra/CS2" }) &&
          JobRunner.RemoveGamePlan(Array.Empty<GameReport>(), "counter_strike_2").Count == 0,
        "T42j the agent remove_game plan deletes EVERY copy case-insensitively (duplicates included) and no-ops when absent");

    Check(dash10.Contains("id=\"mj-modal\"") && dash10.Contains("/api/master-install") &&
          dash10.Contains("/api/master-uninstall") && dash10.Contains("/api/master-remove") &&
          dash10.Contains("mjPush(") && dash10.Contains("display_name||g.game_id"),
        "T42k every master row ships install…/uninstall… (station picker), push (all stations, confirm-gated) and remove (confirm-gated) buttons");

    await app9.StopAsync();
}

// ---------- T43: steam_addid.txt — clients tag games with their Steam AppID ----------
{
    var tagDir = Path.Combine(tmp, "t43tag"); Directory.CreateDirectory(tagDir);
    File.WriteAllText(Path.Combine(tagDir, "steam_addid.txt"), "  # CS2 alias\n 730 \n");
    Check(GameScanner.ProbeSteamAppId(tagDir) == "730",
        "T43a steam_addid.txt is parsed for the first digit run — comment/blank prefixes tolerated");
    var tagDir2 = Path.Combine(tmp, "t43tag2"); Directory.CreateDirectory(tagDir2);
    File.WriteAllText(Path.Combine(tagDir2, "STEAM_ADDID.txt"), "bananas");
    var tagDir3 = Path.Combine(tmp, "t43tag3"); Directory.CreateDirectory(tagDir3);
    Check(GameScanner.ProbeSteamAppId(tagDir2) is null && GameScanner.ProbeSteamAppId(tagDir3) is null,
        "T43b a junk marker or no marker means NO tag — the agent never guesses an AppID");

    var root43 = Path.Combine(tmp, "t43root"); Directory.CreateDirectory(Path.Combine(root43, "CS2"));
    File.WriteAllText(Path.Combine(root43, "CS2", "steam_addid.txt"), "730");
    var (rep43, _) = GameScanner.Scan(new[] { root43 });
    var wire43 = Wire.ToJson(rep43.Single(r => r.GameId == "cs2"));
    Check(rep43.Single(r => r.GameId == "cs2").SteamAppId == "730" && wire43.Contains("\"steam_app_id\":\"730\""),
        "T43c the folder scan picks up each game's steam_addid.txt — reports carry snake_case steam_app_id per schema");

    var mf43 = new MasterManifest(5, DateTimeOffset.UtcNow,
        new List<MasterGame> { new("counter_strike_2", "1.0.5", AppId: "730", DisplayName: "Counter-Strike 2") }, new());
    var r43 = new LocalManifest("c43", "0001", null, DateTimeOffset.UtcNow, "test",
        new JsonObject { ["machineModel"] = "Prime X" },
        new List<GameReport> { new("cs2", "CS2", "/steam/common/CS2", "1.0.4", "steam", "OK", "730") });
    var iss43 = Comparator.Games(r43, mf43);
    Check(!iss43.Any(i => i.Kind == "unmanaged_game") && !iss43.Any(i => i.Kind == "missing_from_machine")
          && iss43.Any(i => i.Kind == "update_required" && i.Subject == "cs2"),
        "T43d an AppID-tagged alias folder pairs with its master entry by AppID — no unmanaged/missing flags, version diff still enforced");

    var dir9c = Path.Combine(tmp, "srv9c");
    Directory.CreateDirectory(dir9c);
    File.WriteAllText(Path.Combine(dir9c, "MasterManifest.json"), Wire.ToJson(new MasterManifest(7, DateTimeOffset.UtcNow,
        new List<MasterGame> { new("counter_strike_2", "abc123", AppId: "730", DisplayName: "Counter-Strike 2") }, new())));
    using var app9c = ServerRole.BuildApp(new EsportsConfig { Role = "server", ServerUrl = "http://0.0.0.0:4394", DataDir = dir9c });
    await app9c.StartAsync();
    using var probe9c = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:4394"), Timeout = TimeSpan.FromSeconds(5) };
    StringContent C9c(object b) => new(Wire.ToJson(b), System.Text.Encoding.UTF8, "application/json");
    Check((await probe9c.PostAsync("/api/register", C9c(new RegisterRequest("t43tag", "9951", "Tagged Bench")))).IsSuccessStatusCode,
        "T43e fixture station registers");
    Check((await probe9c.PostAsync("/api/report", C9c(new LocalManifest("t43tag", "9951", "Tagged Bench", DateTimeOffset.UtcNow,
        "test", new JsonObject { ["machineModel"] = "Prime X" },
        new List<GameReport> { new("cs2", "CS2", "/D/SteamLibrary/steamapps/common/CS2", "unknown", "steam", "OK", "730") })))).IsSuccessStatusCode,
        "T43f a steam_addid-tagged report uploads cleanly (field optional — older agents unaffected)");
    var mi43 = await probe9c.PostAsync("/api/master-install", C9c(new MasterJobRequest("730", "t43tag")));
    var job43 = JsonNode.Parse(File.ReadAllText(Path.Combine(dir9c, "Jobs.json")))!.AsArray()
        .Single(j => j!["kind"]!.GetValue<string>() == "update_game");
    Check((int)mi43.StatusCode == 200 && job43!["path"]!.GetValue<string>() == "/D/SteamLibrary/steamapps/common/CS2",
        "T43g master-install resolves its path through the steam_addid-tagged copy even when the folder name never matches the master id");
    Check(File.ReadAllText(Path.Combine(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..")),
        "schemas", "local_manifest.schema.json")).Contains("\"steam_app_id\""),
        "T43h steam_app_id is a declared, pattern-checked contract field (schemas/local_manifest.schema.json)");
    await app9c.StopAsync();
}

// ---------- T44: capability gate — never queue a verb the station's agent can't run ----------
{
    var dir9d = Path.Combine(tmp, "srv9d"); Directory.CreateDirectory(dir9d);
    File.WriteAllText(Path.Combine(dir9d, "MasterManifest.json"), Wire.ToJson(new MasterManifest(3, DateTimeOffset.UtcNow,
        new List<MasterGame> { new("counter_strike_2", "999", AppId: "730", DisplayName: "Counter-Strike 2") }, new List<FactoryEntry>())));
    using var app9d = ServerRole.BuildApp(new EsportsConfig { Role = "server", ServerUrl = "http://0.0.0.0:4397", DataDir = dir9d });
    await app9d.StartAsync();
    using var probe9d = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:4397"), Timeout = TimeSpan.FromSeconds(5) };
    StringContent C9d(object b) => new(Wire.ToJson(b), System.Text.Encoding.UTF8, "application/json");
    await probe9d.PostAsync("/api/register", C9d(new RegisterRequest("t44old", "9961", "Old Bench")));
    await probe9d.PostAsync("/api/register", C9d(new RegisterRequest("t44new", "9962", "New Bench")));
    var repOld44 = await probe9d.PostAsync("/api/report", C9d(new LocalManifest("t44old", "9961", "Old Bench",
        DateTimeOffset.UtcNow, "0.5.11", new JsonObject { ["machineModel"] = "Prime X" }, new List<GameReport>())));
    await probe9d.PostAsync("/api/report", C9d(new LocalManifest("t44new", "9962", "New Bench",
        DateTimeOffset.UtcNow, AgentInfo.Version, new JsonObject { ["machineModel"] = "Prime X" }, new List<GameReport>())));
    Check(repOld44.IsSuccessStatusCode, "T44a fixture stations report — one pinned to the old 0.5.11 agent, one current");
    var ui44 = JsonNode.Parse(await (await probe9d.PostAsync("/api/master-uninstall", C9d(new MasterJobRequest("730")))).Content.ReadAsStringAsync())!;
    Check(ui44["queued"]!.GetValue<int>() == 1 && ui44["skipped"]!.GetValue<int>() == 1 &&
          ui44["reasons"]!.AsArray().Any(x => x!.GetValue<string>().Contains("too old")),
        "T44b uninstall SKIPS a pre-0.5.12 agent with a clear self-update reason — never a doomed remove_game an old agent refuses as unknown kind");
    var ii44 = JsonNode.Parse(await (await probe9d.PostAsync("/api/master-install", C9d(new MasterJobRequest("730")))).Content.ReadAsStringAsync())!;
    Check(ii44["queued"]!.GetValue<int>() == 1 && ii44["skipped"]!.GetValue<int>() == 1 &&
          ii44["reasons"]!.AsArray().Any(x => x!.GetValue<string>().Contains("too old")),
        "T44c push-install with no resolvable path skips the old agent too — its 0.5.11 code throws 'missing install path' on a null path (the live report)");
    var st44 = JsonNode.Parse(await probe9d.GetStringAsync("/api/state"))!;
    var oldDev44 = st44["devices"]!.AsArray().Single(d => d!["client_id"]!.GetValue<string>() == "t44old");
    Check(oldDev44["agent_version"]!.GetValue<string>() == "0.5.11" &&
          (oldDev44["jobs"] is null || !oldDev44["jobs"]!.AsArray().Any(j => j!["kind"]!.GetValue<string>() is "remove_game" or "update_game")),
        "T44d the old station carries its agent_version chip and received ZERO jobs — refused at queue time, not executed-then-failed");
    await app9d.StopAsync();
    using var app9e = ServerRole.BuildApp(new EsportsConfig { Role = "server", ServerUrl = "http://0.0.0.0:4398", DataDir = dir9d });
    await app9e.StartAsync();
    using var probe9e = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:4398"), Timeout = TimeSpan.FromSeconds(5) };
    var st44r = JsonNode.Parse(await probe9e.GetStringAsync("/api/state"))!;
    var oldDevR = st44r["devices"]!.AsArray().Single(d => d!["client_id"]!.GetValue<string>() == "t44old");
    var ui44r = JsonNode.Parse(await (await probe9e.PostAsync("/api/master-uninstall", C9d(new MasterJobRequest("730")))).Content.ReadAsStringAsync())!;
    Check(oldDevR["agent_version"]!.GetValue<string>() == "0.5.11" &&
          ui44r["queued"]!.GetValue<int>() == 0 &&
          ui44r["reasons"]!.AsArray().Any(x => x!.GetValue<string>().Contains("too old")),
        "T44e after a SERVER RESTART the agent chip and the gate still work from hydrated stored reports — no silent blank-version window until each station re-reports");
    await app9e.StopAsync();
}

// ---------- T45: one job in flight per station (kills concurrent-SteamCMD 'exit 7' collisions) ----------
{
    var dir9f = Path.Combine(tmp, "srv9f"); Directory.CreateDirectory(dir9f);
    File.WriteAllText(Path.Combine(dir9f, "MasterManifest.json"), Wire.ToJson(new MasterManifest(4, DateTimeOffset.UtcNow,
        new List<MasterGame>
        {
            new("alpha_game", "1", AppId: "450001", DisplayName: "Alpha Game"),
            new("beta_game", "1", AppId: "450002", DisplayName: "Beta Game"),
        }, new List<FactoryEntry>())));
    using var app9f = ServerRole.BuildApp(new EsportsConfig { Role = "server", ServerUrl = "http://0.0.0.0:4399", DataDir = dir9f });
    await app9f.StartAsync();
    using var probe9f = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:4399"), Timeout = TimeSpan.FromSeconds(5) };
    StringContent C9f(object b) => new(Wire.ToJson(b), System.Text.Encoding.UTF8, "application/json");
    await probe9f.PostAsync("/api/register", C9f(new RegisterRequest("t45one", "9971", "Solo Bench")));
    async Task<AckEnvelope> Beat45()
    {
        var r = await probe9f.PostAsync("/api/heartbeat", C9f(new HeartbeatRequest("t45one", "9971", DateTimeOffset.UtcNow, "0.5.16")));
        return Wire.FromJson<AckEnvelope>(await r.Content.ReadAsStringAsync())!;
    }
    var q1 = JsonNode.Parse(await (await probe9f.PostAsync("/api/master-install", C9f(new MasterJobRequest("450001")))).Content.ReadAsStringAsync())!;
    var q2 = JsonNode.Parse(await (await probe9f.PostAsync("/api/master-install", C9f(new MasterJobRequest("450002")))).Content.ReadAsStringAsync())!;
    Check(q1["queued"]!.GetValue<int>() == 1 && q2["queued"]!.GetValue<int>() == 1, "T45a two installs queued on one station");
    var beat1 = await Beat45();
    Check(beat1.Job is not null && beat1.Job.State == "queued", "T45b first heartbeat hands out exactly ONE job");
    await probe9f.PostAsync("/api/job_update", C9f(new JobUpdateRequest(beat1.Job!.JobId, "t45one", "running")));
    var beat2 = await Beat45();
    Check(beat2.Job is null, "T45c while a job is RUNNING the heartbeat hands out NO second job — the server never lets one machine run two apps at once (this is what collided two SteamCMDs into 'steamcmd exit 7')");
    await probe9f.PostAsync("/api/job_update", C9f(new JobUpdateRequest(beat1.Job.JobId, "t45one", "succeeded")));
    var beat3 = await Beat45();
    Check(beat3.Job is not null && beat3.Job.Subject == "beta_game", "T45d the moment job one succeeds, the NEXT queued job flows — serial queue, never stalled");
    await probe9f.PostAsync("/api/job_update", C9f(new JobUpdateRequest(beat3.Job!.JobId, "t45one", "running")));
    Check((await Beat45()).Job is null, "T45e second job also runs alone");
    await probe9f.PostAsync("/api/job_update", C9f(new JobUpdateRequest(beat3.Job.JobId, "t45one", "succeeded")));
    Check((await Beat45()).Job is null, "T45f empty queue hands out nothing");

    // Agent-side backstop: a steamcmd orphaned by a crash-restart must be waited out, never collided with.
    // NB: copy a REAL standalone ELF — this box's coreutils is a name-dispatching multicall binary,
    // so a copied 'sleep' renamed to steamcmd execs itself out instantly ("unknown program").
    var fakeSc = Path.Combine(dir9f, "steamcmd");
    var imp = new[] { "/usr/bin/python3.14", "/usr/bin/python3", "/usr/bin/python" }.FirstOrDefault(File.Exists)
              ?? throw new InvalidOperationException("T45 needs a python3 to impersonate an orphan steamcmd");
    File.Copy(imp, fakeSc, overwrite: true);
    var t0 = DateTime.UtcNow;
    using (var orph = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(fakeSc, "-c \"import time;time.sleep(3)\"") { UseShellExecute = false })!)
    {
        var waited = await JobRunner.WaitForForeignSteamCmdAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        var el = DateTime.UtcNow - t0;
        Check(waited && el.TotalSeconds >= 2, "T45g agent waits for a foreign steamcmd to finish before launching its own (no exit-7 collision)");
    }
    using (var orph = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(fakeSc, "-c \"import time;time.sleep(60)\"") { UseShellExecute = false })!)
    {
        var gaveUp = await JobRunner.WaitForForeignSteamCmdAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
        orph.Kill();
        Check(!gaveUp, "T45h a steamcmd that never ends is refused past the bounded wait — job fails with a clear 'refused to collide' reason instead of hanging forever");
    }

    // T45b: resume-retry classification — the live failures were exit 8 (SUSPEND: the station's own
    // Steam client preempted steamcmd's download), which a re-run simply resumes.
    Check(JobRunner.IsTransientSteamCmdExit(7) && JobRunner.IsTransientSteamCmdExit(8) &&
          JobRunner.IsTransientSteamCmdExit(12) && JobRunner.IsTransientSteamCmdExit(26),
        "T45i connection/SUSPEND/unavailable/disconnect codes are transient — the install re-runs and resumes the partial download");
    Check(!JobRunner.IsTransientSteamCmdExit(2) && !JobRunner.IsTransientSteamCmdExit(15) &&
          !JobRunner.IsTransientSteamCmdExit(19) && !JobRunner.IsTransientSteamCmdExit(33),
        "T45j disk/license/corrupt-path codes fail FAST — never four pointless retries on a real failure");

    // T46: game-pill visual semantics on the dashboard (CSS + chip labels, asserted on source).
    var dash46 = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "wwwroot", "dashboard.html"));
    Check(dash46.Contains("@keyframes esu-opulse") &&
          dash46.Contains(".cchip.up{color:#f97316") && dash46.Contains("animation:esu-opulse"),
        "T46a out-of-date game chips carry the ORANGE pulsing animation (⚠ + esu-opulse), not static blue");
    Check(dash46.Contains(".cchip.mi{color:var(--info") && dash46.Contains("box-shadow:0 0 8px color-mix(in srgb,var(--info"),
        "T46b master-list-but-missing game chips (＋) GLOW blue via info-colored box-shadow");
    Check(dash46.Contains(".cchip.ung{color:var(--mut)}") &&
          dash46.Contains("update_required:'⚠'") && dash46.Contains("unmanaged_game:'?'") && dash46.Contains("duplicate_resolved:'ung'"),
        "T46c '?' unmanaged games stay grey, ⚠ marks stale games, duplicates fold to grey — the three states read at a glance");
    await app9f.StopAsync();
}

Directory.Delete(tmp, true);
Console.WriteLine($"\n{(failed == 0 ? "ALL TESTS PASSED" : $"{failed} TEST(S) FAILED")}");
return failed;

static double ProcCpu(int pid)
{
    try
    {
        var f = File.ReadAllText($"/proc/{pid}/stat").Split(' ');
        return (long.Parse(f[13]) + long.Parse(f[14])) / 100.0; // utime+stime, USER_HZ=100
    }
    catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException or FormatException)
    {
        return double.NaN;   // probe pid died mid-gate: fail the Check line, not the whole suite
    }
}
static double ProcPeakRssMb(int pid)
{
    try
    {
        foreach (var line in File.ReadAllLines($"/proc/{pid}/status"))
            if (line.StartsWith("VmHWM:")) return double.Parse(line.Split(default(char[]), StringSplitOptions.RemoveEmptyEntries)[1]) / 1024.0;
        return double.MaxValue;
    }
    catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException or FormatException)
    {
        return double.NaN;   // same dead-pid race guard as ProcCpu
    }
}
static bool Throws<T>(Action a) where T : Exception
{
    try { a(); return false; } catch (T) { return true; }
}

// minimal shape for reading /api/state
file sealed record JsonDoc(List<Dev> devices, List<AlertDoc> alerts);
file sealed record Dev(string client_id, int? games, int? duplicates, List<IssueDoc> issues, List<JobDoc>? jobs, string? display_name = null, string? location = null, string? device_pin = null, int? wu_pending = null, string? wu_status = null, bool wallpaper_drift = false, List<AppDoc>? apps = null, List<InstAppDoc>? installed_apps = null, string? desired_state = null, string? wu_install_state = null, int? wu_install_total = null, int? cleared_issues = null, string? machine_model = null, string? agent_version = null);
file sealed record AppDoc(string app_id, bool installed, string? version, bool? up_to_date, long checked_unix);
file sealed record InstAppDoc(string name, string? publisher, string? version, string hive, string view);

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
file sealed record JobDoc(string job_id, string kind, string subject, string state, string? detail, int? progress = null);
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
