using System.Diagnostics;
using System.Security.Cryptography;
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
var loopTask = loop.RunAsync(cts.Token);

Check(await Until(async () => (await State()).devices.Any(d => d.games >= 2 && d.duplicates == 1)),
    "T8a agent registered + manifest reported (with duplicate flagged)");

var cid = (await State()).devices[0].client_id;
Check(await PostAck("/api/rename", new RenameRequest(cid, "LAN-PC-TEST")), "T8b operator rename via API");
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
file sealed record Dev(string client_id, int? games, int? duplicates, List<IssueDoc> issues, List<JobDoc>? jobs);
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
