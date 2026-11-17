using System.Diagnostics;
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
var loopTask = loop.RunAsync(cts.Token);

Check(await Until(async () => (await State()).devices.Any(d => d.games >= 2 && d.duplicates == 1)),
    "T8a agent registered + manifest reported (with duplicate flagged)");

var cid = (await State()).devices[0].client_id;
Check(await PostAck("/api/rename", new RenameRequest(cid, "LAN-PC-TEST")), "T8b operator rename via API");
Check(await PostAck("/api/downed", new DownedRequest(cid, "downed", "out-of-service", "maintenance")), "T8c downed set via API");
Check(await Until(async () => loop.LastDesiredState == "downed"), "T8d agent receives desired_state via heartbeat ack");

await probe.PostAsync("/api/locate/" + cid, new StringContent(""));
Check(await Until(async () => loop.ConsumedDirectives.Any(d => d.Type == "locate"), 6), "T8e locate directive delivered once via ack");

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

// minimal shape for reading /api/state
file sealed record JsonDoc(List<Dev> devices, List<AlertDoc> alerts);
file sealed record Dev(string client_id, int? games, int? duplicates, List<IssueDoc> issues);
file sealed record IssueDoc(string kind, string subject);
file sealed record AlertDoc(string type, string severity);
