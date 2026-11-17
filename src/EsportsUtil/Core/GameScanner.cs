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
