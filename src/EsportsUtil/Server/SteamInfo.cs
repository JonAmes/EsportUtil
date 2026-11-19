using System.Diagnostics;
using System.Text.RegularExpressions;
using EsportsUtil.Core;

namespace EsportsUtil.Server;

/// <summary>T41: resolve a Steam AppID to (game name, depot BuildID) by running steamcmd
/// "+login anonymous +app_info_print &lt;appid&gt; +quit" — the operator's pipeline, but parsed
/// server-side instead of awk'd: the raw VDF carries lowercase quoted "buildid" once per depot,
/// so field-position awk silently returns nothing. The BuildID recorded is the depot keyed by the
/// AppID itself (the main depot), falling back to the first depot seen.
/// Test seam: ESU_FAKE_STEAMCMD holds a shell command substituted for steamcmd.</summary>
public static class SteamInfo
{
    public sealed record SteamApp(string AppId, string Name, string BuildId);

    /// <summary>Locate steamcmd: explicit config path first, then STEAMCMD env, then the usual
    /// install spots, then PATH. Null = not installed (the endpoint says so instead of guessing).</summary>
    public static string? Locate(EsportsConfig cfg)
    {
        // The test seam substitutes for steamcmd entirely — never require a real install for it.
        if (Environment.GetEnvironmentVariable("ESU_FAKE_STEAMCMD") is not null) return "seam";
        var cands = new List<string?> { cfg.SteamcmdPath, Environment.GetEnvironmentVariable("STEAMCMD") };
        var home = Environment.GetEnvironmentVariable("HOME") ??
                   Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home)) { cands.Add(Path.Combine(home, "steamcmd", "steamcmd.sh")); cands.Add(Path.Combine(home, "steamcmd", "steamcmd")); }
        cands.Add("/opt/steamcmd/steamcmd.sh");
        foreach (var p in cands)
            if (!string.IsNullOrWhiteSpace(p) && File.Exists(p!)) return p;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var f = Path.Combine(dir, "steamcmd");
            if (File.Exists(f)) return f;
        }
        return null;
    }

    // VDF shapes (tab-indented): app-level name at depth 1; depot keys and buildids under "depots".
    private static readonly Regex NameRe = new(@"^\t\t""name""\s+""(.+)""$", RegexOptions.Compiled);
    private static readonly Regex DepotRe = new(@"^\t\t\t""([^""\s]+)""$", RegexOptions.Compiled);
    private static readonly Regex OpenRe = new(@"^\t\t\t\($", RegexOptions.Compiled);
    private static readonly Regex BuildRe = new(@"^\t\t\t\t""buildid""\s+""(\d+)""$", RegexOptions.Compiled);

    public static SteamApp? Parse(string output, string appId)
    {
        string? name = null, primary = null, first = null, depot = null, pending = null;
        foreach (var raw in output.Split('\n'))
        {
            var l = raw.TrimEnd('\r');
            if (name is null)
            {
                var nm = NameRe.Match(l);
                if (nm.Success) { name = nm.Groups[1].Value; continue; }
            }
            var dk = DepotRe.Match(l);
            if (dk.Success) { pending = dk.Groups[1].Value; continue; }
            if (pending != null) { if (OpenRe.IsMatch(l)) depot = pending; pending = null; }
            var b = BuildRe.Match(l);
            if (b.Success) { first ??= b.Groups[1].Value; if (depot == appId) primary ??= b.Groups[1].Value; }
        }
        return name is null ? null : new SteamApp(appId, name, primary ?? first ?? "");
    }

    /// <summary>Run steamcmd for the AppID and parse it. Returns (app, "") on success or
    /// (null, human-readable error) — callers map that to 502/503, never to a guessed entry.</summary>
    public static async Task<(SteamApp? app, string error)> ResolveAsync(string appId, string? steamcmd)
    {
        // appId reaches a shell only after the caller's ^\d{1,10}$ gate; the seam is test-only.
        var fake = Environment.GetEnvironmentVariable("ESU_FAKE_STEAMCMD");
        var psi = new ProcessStartInfo();
        if (fake is not null) { psi.FileName = "/bin/sh"; psi.ArgumentList.Add("-c"); psi.ArgumentList.Add($"{fake} {appId}"); }
        else
        {
            psi.FileName = steamcmd!;
            foreach (var a in new[] { "+login", "anonymous", "+app_info_print", appId, "+quit" }) psi.ArgumentList.Add(a);
        }
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;

        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(150));
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { }
            return (null, "steamcmd timed out resolving the AppID");
        }
        var app = Parse(await stdout, appId);
        if (app is null) return (null, $"steamcmd found no app info for AppID {appId} — wrong ID or not a public app");
        if (app.BuildId.Length == 0) return (null, $"AppID {appId} ({app.Name}) has no depot buildid — nothing downloadable to record");
        return (app, "");
    }
}
