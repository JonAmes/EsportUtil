using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EsportsUtil.Core;

/// <summary>A published GitHub release asset we can swap in.</summary>
public sealed record ReleaseInfo(string Tag, string DownloadUrl, long Size, string? Sha256);

/// <summary>One self-update verdict (mirrors the UpdateWatch/SteamCmdWatch beat-hook shape).
/// state: up_to_date | update_available | downloading | ready | restarting | restart_pending | failed | error.</summary>
public sealed record SelfUpdateVerdict(string Current, string? Latest, string State, int? Progress, string? Error, long CheckedUnix)
{
    public JsonNode ToNode() => new JsonObject
    {
        ["current"] = Current,
        ["latest"] = Latest,
        ["state"] = State,
        ["progress"] = Progress,
        ["error"] = Error,
        ["checked_unix"] = CheckedUnix,
    };
}

/// <summary>Self-update (PLAN.md T30): poll the venue repo's GitHub Releases for a newer
/// esportsutil.exe, stream the asset directly from GitHub over https, verify its sha256 digest,
/// then hand off: the NEW staged EXE waits for this process to exit, copies itself over the old
/// path, and relaunches in tray mode. Service hosts download+verify but never replace a locked
/// running image (restart_pending; the SCM restarts the replaced file).
/// All decision logic is pure and Linux-testable; only the handoff is Windows-only.</summary>
public static class SelfUpdateWatch
{
    public const string FakeEnvVar = "ESU_FAKE_RELEASE";       // "tag|url" seam for tests/demo, same style as ESU_FAKE_UPDATES
    public const string FinalizeFlag = "--replace-finalize";
    public const string AssetName = "esportsutil.exe";
    public const int MinDownloadBytes = 1024 * 1024;           // a truncated release never replaces a working EXE

    /// <summary>Set once by Program: service hosts defer the file swap (session-0 image is locked).</summary>
    public static bool HostAllowsSwap = true;

    public static SelfUpdateVerdict? Last;
    static long _lastCheckUnix;

    public static string CurrentVersion =>
        typeof(SelfUpdateWatch).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";

    public static string NormalizeTag(string tag) => tag.StartsWith('v') ? tag[1..] : tag;

    public static bool NewerThan(string current, string latest) =>
        AppCatalog.VersionCompare(latest, current) > 0;

    public static bool Due(long nowUnix, long lastCheckUnix, int intervalMin) =>
        intervalMin <= 0 ? false : nowUnix - lastCheckUnix >= intervalMin * 60L;

    public static string StagePath(string dataDir) => Path.Combine(dataDir, "updates", AssetName + ".new");

    /// <summary>argv for the handoff child: the staged exe replaces the target then relaunches tray.</summary>
    public static List<string> FinalizeArgs(string targetExe, int parentPid) =>
        new() { FinalizeFlag, targetExe, "--ppid", parentPid.ToString(), "--tray" };

    /// <summary>Pick the release payload's asset literally named esportsutil.exe; GitHub's
    /// optional asset digest ("sha256:...") becomes the verification hash.</summary>
    public static ReleaseInfo? ParseRelease(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            if (string.IsNullOrWhiteSpace(tag) || !root.TryGetProperty("assets", out var assets)) return null;
            foreach (var a in assets.EnumerateArray())
            {
                var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                var url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                if (name == AssetName && !string.IsNullOrWhiteSpace(url))
                {
                    long size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var sz) ? sz : 0;
                    string? sha = a.TryGetProperty("digest", out var d) && d.GetString() is string dg && dg.StartsWith("sha256:")
                        ? dg["sha256:".Length..] : null;
                    return new ReleaseInfo(tag!, url!, size, sha);
                }
            }
            return null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Beat hook: rate-limited GitHub check; on a newer release, download+verify
    /// fire-and-forget. The heartbeat never waits on GitHub.</summary>
    public static async Task CheckAsync(HttpClient http, EsportsConfig cfg, CancellationToken ct, bool force = false)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (_applying) return;   // staged apply in flight (or done): never re-check over live progress
        var fake = Environment.GetEnvironmentVariable(FakeEnvVar);
        if (!force && fake is null && !Due(now, _lastCheckUnix, cfg.SelfUpdateCheckIntervalMin)) return;
        _lastCheckUnix = now;

        ReleaseInfo? rel = null;
        string? err = null;
        try
        {
            if (fake is not null)
            {
                var bits = fake.Split('|');
                rel = bits.Length >= 2 && bits[1].Length > 0 ? new ReleaseInfo(bits[0], bits[1], 0, null) : null;
                if (rel is null) err = "ESU_FAKE_RELEASE malformed";
            }
            else
            {
                using var msg = new HttpRequestMessage(HttpMethod.Get,
                    $"https://api.github.com/repos/{cfg.GitHubRepo}/releases/latest");
                msg.Headers.UserAgent.ParseAdd("esportsutil-agent");
                using var resp = await http.SendAsync(msg, ct);
                if (resp.IsSuccessStatusCode) rel = ParseRelease(await resp.Content.ReadAsStringAsync(ct));
                else err = $"GitHub API {(int)resp.StatusCode}";
                if (rel is null && err is null) err = "latest release has no " + AssetName + " asset";
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { err = e.Message; }

        var latest = rel is null ? null : NormalizeTag(rel.Tag);
        var state = err is not null ? "error"
            : NewerThan(CurrentVersion, latest!) ? "update_available" : "up_to_date";
        Last = new SelfUpdateVerdict(CurrentVersion, latest, state, null, err, now);
        if (state != "update_available" || !cfg.SelfUpdateApply || _applying) return;
        _applying = true;   // one in-flight apply; a staged "ready" download is never re-fetched
        _ = Task.Run(() => ApplyAsync(http, rel!, cfg, ct));
    }

    /// <summary>Reset for tests.</summary>
    public static void Reset() { Last = null; _lastCheckUnix = 0; _applying = false; }
    static bool _applying;

    static async Task ApplyAsync(HttpClient http, ReleaseInfo rel, EsportsConfig cfg, CancellationToken ct)
    {
        // https is mandatory for our own EXE too — except under the explicit test/demo seam.
        if (!rel.DownloadUrl.StartsWith("https://") && Environment.GetEnvironmentVariable(FakeEnvVar) is null)
        {
            Last = Last! with { State = "failed", Error = "release asset is not https" };
            return;
        }
        try
        {
            var stage = StagePath(cfg.DataDir);
            Directory.CreateDirectory(Path.GetDirectoryName(stage)!);
            await DownloadAsync(http, rel.DownloadUrl, stage,
                p => Last = Last! with { State = "downloading", Progress = p }, ct);

            if (Environment.GetEnvironmentVariable(FakeEnvVar) is null && new FileInfo(stage).Length < MinDownloadBytes)
                throw new InvalidDataException("downloaded release is implausibly small");
            if (rel.Sha256 is not null)
            {
                using var fs = File.OpenRead(stage);
                var hex = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
                if (hex != rel.Sha256.ToLowerInvariant())
                    throw new InvalidDataException("sha256 mismatch — release asset rejected");
            }
            Last = Last! with { State = "ready", Progress = 100, Error = null };

            if (!OperatingSystem.IsWindows()) return;      // swap itself is a Windows-box gate (BUILD.md)
            if (!HostAllowsSwap)
            {
                Last = Last! with { State = "restart_pending",
                    Error = "service host: the SCM restarts the staged file — 'esportsutil service stop && start'" };
                return;
            }
            var target = Environment.ProcessPath;
            if (target is null || !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileNameWithoutExtension(target).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            {
                // Running as `dotnet esportsutil.dll` (dev) — never target the dotnet host itself.
                Last = Last! with { State = "restart_pending", Error = "downloaded+verified; run the published EXE for auto-handoff" };
                return;
            }
            // Handoff: the NEW staged exe waits for us to exit, replaces the old path, relaunches tray.
            Process.Start(new ProcessStartInfo(stage, string.Join(' ', FinalizeArgs(target, Environment.ProcessId)))
            { UseShellExecute = false });
            Last = Last! with { State = "restarting", Error = null };
            await Task.Delay(750, CancellationToken.None);  // let the beat flush its report
            Environment.Exit(0);
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Last = Last! with { State = "failed", Error = e.Message }; }
    }

    static async Task DownloadAsync(HttpClient http, string url, string dest, Action<int?> progress, CancellationToken ct)
    {
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        long? total = resp.Content.Headers.ContentLength;
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(dest);
        var buf = new byte[64 * 1024];
        long done = 0;
        int lastPct = -1;
        var lastReport = DateTime.MinValue;
        while (true)
        {
            int n = await src.ReadAsync(buf, ct);
            if (n == 0) break;
            await dst.WriteAsync(buf.AsMemory(0, n), ct);
            done += n;
            if (total is > 0)
            {
                var pct = (int)(done * 100 / total.Value);
                if (pct != lastPct && DateTime.UtcNow - lastReport >= TimeSpan.FromSeconds(1))
                {
                    progress(pct);
                    lastPct = pct;
                    lastReport = DateTime.UtcNow;
                }
            }
        }
    }

    /// <summary>Runs inside the NEW staged exe: wait for the old process to exit, replace the
    /// target path, relaunch it detached in tray mode, then drop the stage copy.</summary>
    public static async Task<int> FinalizeAsync(string targetExe, int parentPid, int maxWaitMs = 20000)
    {
        var stage = Environment.ProcessPath!;   // we ARE running from the staged file
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < maxWaitMs)
        {
            try { if (Process.GetProcessById(parentPid).HasExited) break; }
            catch (ArgumentException) { break; }   // parent already gone
            await Task.Delay(250);
        }
        File.Copy(stage, targetExe, overwrite: true);
        Process.Start(new ProcessStartInfo(targetExe, "--tray") { UseShellExecute = false });
        try { File.Delete(stage); } catch (IOException) { }
        return 0;
    }
}
