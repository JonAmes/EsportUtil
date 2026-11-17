using System.Runtime.Versioning;
using Microsoft.Win32;

namespace EsportsUtil.Core;

/// <summary>Folder-structure game discovery (PLAN Phase 1, milestone 1): each immediate
/// subdirectory of a configured install root is a game; the folder name normalizes to game_id.
/// Duplicate game_ids across roots are reported — resolution is the server's decision, never ours.</summary>
public static class GameScanner
{
    public static string NormalizeFolder(string folder)
    {
        var s = folder.ToLowerInvariant();
        s = new string(s.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
        while (s.Contains("__")) s = s.Replace("__", "_");
        return s.Trim('_');
    }

    public static string DetectLauncher(string installRoot)
    {
        var p = installRoot.ToLowerInvariant().Replace('\\', '/');
        if (p.Contains("steam")) return "steam";
        if (p.Contains("epic")) return "epic";
        if (p.Contains("gog")) return "gog";
        if (p.Contains("battlenet") || p.Contains("battle.net")) return "battlenet";
        return "other";
    }

    /// <summary>The default Steam library's installed-games root ("C:\Program Files (x86)\Steam\
    /// steamapps\common" on Windows). Empty when the location does not exist on this machine.</summary>
    public static string DefaultSteamRoot()
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (string.IsNullOrEmpty(pf)) return "";
        var root = Path.Combine(pf, "Steam", "steamapps", "common");
        return Directory.Exists(root) ? root : "";
    }

    /// <summary>ESU_FAKE_STEAM_ROOTS ('|'-separated paths) forces Steam scan roots on any OS —
    /// tests/demos, same seam style as ESU_FAKE_UPDATES.</summary>
    public const string FakeSteamRootsEnvVar = "ESU_FAKE_STEAM_ROOTS";

    /// <summary>Registry InstallPath + SteamLibrary values → the existing "steamapps/common" roots,
    /// in first-seen order, deduped case-insensitively. Pure (exists-injected) so Steam-on-Windows
    /// geometry is unit-testable anywhere; a library whose common dir doesn't exist is dropped.</summary>
    public static List<string> SteamRootsFromRegistryValues(string? installPath, IEnumerable<string> libraryPaths, Func<string, bool> dirExists)
    {
        var roots = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in new[] { installPath }.Concat(libraryPaths))
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            var root = Path.Combine(p, "steamapps", "common");
            if (dirExists(root) && seen.Add(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))
                roots.Add(root);
        }
        return roots;
    }

    /// <summary>Where Steam actually lives, per the Windows registry — the hardcoded default root
    /// misses every non-default install (T31). HKLM SOFTWARE\Valve\Steam InstallPath (32-bit view
    /// first: that's where a 64-bit process finds Steam) plus this user's HKCU SOFTWARE\Valve\Steam
    /// SteamLibrary* values (extra libraries from the Steam settings).</summary>
    [SupportedOSPlatform("windows")]
    public static List<string> SteamRootsFromRegistry()
    {
        string? installPath = null;
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            if (installPath is not null) break;
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = baseKey.OpenSubKey(@"SOFTWARE\Valve\Steam");
                installPath = key?.GetValue("InstallPath") as string;
            }
            catch (Exception) { }
        }
        var libs = new List<string>();
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(@"SOFTWARE\Valve\Steam");
            if (key is not null)
                foreach (var v in key.GetValueNames())
                    if (v.StartsWith("SteamLibrary", StringComparison.OrdinalIgnoreCase) && key.GetValue(v) is string s)
                        libs.Add(s);
        }
        catch (Exception) { }
        return SteamRootsFromRegistryValues(installPath, libs, Directory.Exists);
    }

    /// <summary>Steam roots the agent scans: fake seam when set, else registry roots + the classic
    /// default (both when the registry knows nothing). Downstream dedupe via EffectiveScanDirs.</summary>
    public static IEnumerable<string> SteamScanRoots()
    {
        var fake = Environment.GetEnvironmentVariable(FakeSteamRootsEnvVar);
        if (!string.IsNullOrWhiteSpace(fake))
            return fake.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var roots = OperatingSystem.IsWindows() ? SteamRootsFromRegistry() : new List<string>();
        var def = DefaultSteamRoot();
        if (def.Length > 0) roots.Add(def);
        return roots;
    }

    /// <summary>Configured roots plus the default Steam root (operator request: Steam games
    /// installed under the default library must be visible without extra config). Case-insensitive
    /// dedupe on trailing-separator-normalized paths; configured order preserved; empty entries
    /// dropped. Pure so the merge is unit-testable on any OS.</summary>
    public static List<string> EffectiveScanDirs(IEnumerable<string> configured, string steamRoot)
        => EffectiveScanDirs(configured, new[] { steamRoot });

    public static List<string> EffectiveScanDirs(IEnumerable<string> configured, IEnumerable<string> steamRoots)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        foreach (var d in configured.Concat(steamRoots))
        {
            if (string.IsNullOrWhiteSpace(d)) continue;
            var key = d.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (seen.Add(key)) list.Add(d);
        }
        return list;
    }

    /// <summary>Version probe order: version.txt / VERSION.txt / version file in the game folder.
    /// Unknown versions report as "unknown" — the Master Manifest comparison (Phase 2) acts on them.</summary>
    public static string ProbeVersion(string gameDir)
    {
        foreach (var name in new[] { "version.txt", "VERSION.txt", "version", "VERSION" })
        {
            var f = Path.Combine(gameDir, name);
            if (File.Exists(f))
            {
                try { return File.ReadAllLines(f).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim() ?? "unknown"; }
                catch (IOException) { }
            }
        }
        return "unknown";
    }

    public static (List<GameReport> Reports, List<DuplicateReport> Duplicates) Scan(IEnumerable<string> scanDirs)
    {
        var reports = new List<GameReport>();
        var byId = new Dictionary<string, List<string>>();

        foreach (var root in scanDirs)
        {
            if (!Directory.Exists(root)) continue;
            string[] subs;
            try { subs = Directory.GetDirectories(root); }
            catch (IOException) { continue; }

            foreach (var dir in subs)
            {
                var folder = Path.GetFileName(dir);
                if (folder.StartsWith(".") || folder.Equals("steamapps", StringComparison.OrdinalIgnoreCase)
                                         || folder.Equals("common", StringComparison.OrdinalIgnoreCase))
                    continue;
                var id = NormalizeFolder(folder);
                if (id.Length == 0) continue;
                reports.Add(new GameReport(id, folder, dir, ProbeVersion(dir), DetectLauncher(root), "OK"));
                if (!byId.TryGetValue(id, out var paths)) byId[id] = paths = new List<string>();
                paths.Add(dir);
            }
        }

        var dupes = byId.Where(kv => kv.Value.Count > 1)
                        .Select(kv => new DuplicateReport(kv.Key, kv.Value)).ToList();
        foreach (var d in dupes)
            for (var i = 0; i < reports.Count; i++)
                if (reports[i].GameId == d.GameId)
                    reports[i] = reports[i] with { Status = "DUPLICATE_FOUND" };

        return (reports, dupes);
    }
}
