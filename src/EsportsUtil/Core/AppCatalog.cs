using System.Text.Json;
using System.Text.Json.Nodes;

namespace EsportsUtil.Core;

/// <summary>One venue app the operator can install/update from the Settings page.
/// InstallerUrl is a direct vendor download (verified against microsoft/winget-pkgs manifests);
/// Args are silent-install switches for the installer itself. VersionApi is the vendor endpoint
/// that names the current release — null means the app self-updates in place and "up to date"
/// is genuinely indeterminate, which we report as null rather than fake green.</summary>
public sealed record AppCatalogEntry(
    string Id, string Name, string InstallerUrl, string Args, string? VersionApi);

/// <summary>Per-app verdict a machine reports for one catalog entry.</summary>
public sealed record AppStatus(
    string AppId, bool Installed, string? Version, bool? UpToDate, long CheckedUnix);

public static class AppCatalog
{
    // Direct vendor URLs, pinned from the winget-pkgs manifests (2026-11). These are the vendors'
    // own "always latest" endpoints, so a fresh download is also the update path.
    public static readonly IReadOnlyList<AppCatalogEntry> All =
    [
        new("chrome", "Google Chrome",
            "https://dl.google.com/tag/s/dl/chrome/install/googlechromestandaloneenterprise64.msi",
            "/qn",
            "https://versionhistory.googleapis.com/v1/chrome/platforms/win/channels/stable/versions/latest"),
        new("firefox", "Mozilla Firefox",
            "https://download.mozilla.org/?product=firefox-latest-ssl&os=win64&lang=en-US",
            "-msa",
            "https://product-details.mozilla.org/1.0/firefox_versions.json"),
        new("steam", "Steam",
            "https://cdn.cloudflare.steamstatic.com/client/installer/SteamSetup.exe",
            "", null),
        new("epic", "Epic Games Launcher",
            "https://launcher-public-service-prod06.ol.epicgames.com/launcher/api/installer/download/EpicGamesLauncherInstaller.msi",
            "/qn", null),
        new("battlenet", "Blizzard Battle.net",
            "https://down-www-us.blizzard.com/download/b.net/index.php?platform=win&locale=enUS",
            "/quiet", null),
        new("riot", "Riot Client",
            "https://lol.secure.dyn.riotcdn.net/channels/public/x/installer/current/live.na.exe",
            "--skip-to-install", null),   // bootstrapper installs the Riot Client itself
    ];

    public static AppCatalogEntry? Entry(string? id) => id is null ? null :
        All.FirstOrDefault(e => e.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Uninstall-registry DisplayName each app installs under (install/version probe).</summary>
    public static string RegistryNameOf(string id) => id.ToLowerInvariant() switch
    {
        "chrome" => "Google Chrome",
        "firefox" => "Mozilla Firefox",
        "steam" => "Steam",
        "epic" => "Epic Games Launcher",
        "battlenet" => "Battle.net",
        "riot" => "Riot Client",
        _ => id,
    };

    /// <summary>MSI URLs go through msiexec; everything else runs as a plain exe. Decision is on
    /// the URL path only (query strings like battle.net's redirect must not confuse it).</summary>
    public static bool IsMsiUrl(string url)
    {
        var path = url.Contains('?') ? url[..url.IndexOf('?')] : url;
        return path.EndsWith(".msi", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Dot-numeric compare ("157.0.8103.79"); missing segments count as 0, non-numeric
    /// tails stop the comparison. Return value sign only.</summary>
    public static int VersionCompare(string a, string b)
    {
        var pa = a.Split('.'); var pb = b.Split('.');
        for (var i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            var x = i < pa.Length ? pa[i] : "0";
            var y = i < pb.Length ? pb[i] : "0";
            var nx = int.TryParse(x, out var vx) ? vx : 0;
            var ny = int.TryParse(y, out var vy) ? vy : 0;
            if (nx != ny) return nx < ny ? -1 : 1;
            if (nx == ny && x != y) return string.CompareOrdinal(x, y);   // build tags ("1.0-beta")
        }
        return 0;
    }

    /// <summary>Outdated verdict: true only when BOTH versions are known and installed < latest.
    /// Unknown latest (self-updating launchers) → null = indeterminate, never false-positive.</summary>
    public static bool? Outdated(string? installed, string? latest)
        => installed is null || latest is null ? null
         : VersionCompare(installed, latest) < 0;

    /// <summary>Inverse polarity for the machine-state "up_to_date" field: true only when both
    /// sides known and installed >= latest; null stays indeterminate (never claim green).</summary>
    public static bool? UpToDate(string? installed, string? latest)
        => Outdated(installed, latest) is { } o ? !o : null;

    /// <summary>Progress percent for a streamed download; null when total size is unknown —
    /// the job still runs, the bar just renders indeterminate.</summary>
    public static int? ProgressPercent(long done, long total)
        => total > 0 ? (int)Math.Clamp(done * 100 / total, 0, 100) : null;

    /// <summary>Vendor JSON → current version string (Chrome versionhistory / Firefox product-details).</summary>
    public static string? ParseLatest(string? apiId, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var el = doc.RootElement;
        return apiId switch
        {
            "chrome" => el.TryGetProperty("version", out var v) ? v.GetString() : null,
            "firefox" => el.TryGetProperty("LATEST_FIREFOX_VERSION", out var f) ? f.GetString() : null,
            _ => null,
        };
    }

    /// <summary>Pure projection: reported installedApps inventory + known latest versions →
    /// one AppStatus per catalog entry. No registry or network — feeds machine_state.apps.
    /// checkedUnix must be CACHE-stable (the version-fetch time), never "now" — the machine-state
    /// content hash would otherwise flip every report and defeat the report dedupe.</summary>
    public static List<AppStatus> Statuses(JsonNode? installedApps, Func<string, string?> latestOf, long checkedUnix)
    {
        var versions = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (installedApps is JsonArray arr)
            foreach (var a in arr)
            {
                var name = a?["display_name"]?.GetValue<string>();
                var ver = a?["display_version"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(name) && !versions.ContainsKey(name)) versions[name] = ver;
            }
        return All.Select(e =>
        {
            var regName = RegistryNameOf(e.Id);
            var installed = versions.TryGetValue(regName, out var ver);
            var latest = latestOf(e.Id);
            return new AppStatus(e.Id, installed, installed ? ver : null,
                installed ? UpToDate(ver, latest) : null, checkedUnix);
        }).ToList();
    }

    public static JsonArray ToNode(IEnumerable<AppStatus> statuses)
    {
        var arr = new JsonArray();
        foreach (var s in statuses)
            arr.Add(new JsonObject
            {
                ["app_id"] = s.AppId,
                ["installed"] = s.Installed,
                ["version"] = s.Version,
                ["up_to_date"] = s.UpToDate,
                ["checked_unix"] = s.CheckedUnix,
            });
        return arr;
    }
}

/// <summary>Agent-side vendor "latest version" cache. Two cheap GETs (Chrome, Firefox) refreshed
/// at process start, after each app install job, and whenever the cache ages past LatestTtlSec.
/// ESU_FAKE_APP_VERSIONS="chrome=1.2.3,firefox=2.0" short-circuits the network for tests/demos,
/// same seam style as ESU_FAKE_UPDATES.</summary>
public static class AppVersionCache
{
    public const string FakeEnvVar = "ESU_FAKE_APP_VERSIONS";
    public const int LatestTtlSec = 12 * 3600;

    private static readonly Dictionary<string, (string Ver, long Unix)> _latest = new(StringComparer.OrdinalIgnoreCase);
    private static long _fetchedUnix;
    private static bool _fetching;

    public static void Reset() { _latest.Clear(); _fetchedUnix = 0; _fetching = false; }

    public static string? LatestOf(string appId)
        => _latest.TryGetValue(appId, out var hit) && DateTimeOffset.UtcNow.ToUnixTimeSeconds() - hit.Unix <= LatestTtlSec
            ? hit.Ver : null;

    /// <summary>Stable stamp for machine_state.apps checked_unix (never "now" — report dedupe).</summary>
    public static long FetchedUnix => _fetchedUnix;

    /// <summary>Fire-and-forget refresh when the cache is empty/stale. Idempotent per TTL window.</summary>
    public static void RefreshIfStale(HttpClient http)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (_fetchedUnix != 0 && now - _fetchedUnix <= LatestTtlSec) return;
        if (_fetching) return;
        _fetching = true;
        _ = Task.Run(async () =>
        {
            try
            {
                var fake = Environment.GetEnvironmentVariable(FakeEnvVar);
                foreach (var e in AppCatalog.All.Where(e => e.VersionApi is not null))
                {
                    string? ver;
                    if (fake is { Length: > 0 })
                        ver = fake.Split(',')
                            .Select(p => p.Split('=', 2))
                            .FirstOrDefault(kv => kv.Length == 2 && kv[0].Trim().Equals(e.Id, StringComparison.OrdinalIgnoreCase))?
                            [1].Trim();
                    else
                        try { ver = AppCatalog.ParseLatest(e.Id, await http.GetStringAsync(e.VersionApi!).ConfigureAwait(false)); }
                        catch (Exception) { ver = null; }   // next beat/next boot retries; verdict stays null
                    if (ver is not null)
                        _latest[e.Id] = (ver, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                }
                _fetchedUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            }
            finally { _fetching = false; }
        });
    }

    /// <summary>After an install job lands, drop the cache so the next projection refetches.</summary>
    public static void Invalidate() { _latest.Clear(); _fetchedUnix = 0; }
}
