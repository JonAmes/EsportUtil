using System.Runtime.Versioning;

namespace EsportsUtil.Core;

/// <summary>Wallpaper primitive — the Phase 4 downed-state mechanism: downed swaps the desktop
/// wallpaper to the Content Library out-of-service image, clear restores the machine_state baseline.
/// Windows: SystemParametersInfo(SPI_SETDESKWALLPAPER) with the file copied out of the content cache
/// (the image must outlive the cache eviction). Linux dev box: the same state transitions are applied
/// to wallpaper-state.json so the convergence loop is fully observable in E2E tests; only the final
/// SPI call is platform-gated.</summary>
public static class Wallpaper
{
    public static string StatePath(string dataDir) => Path.Combine(dataDir, "wallpaper-state.json");

    public sealed record State(string? Baseline, string? Current, string? ContentId);

    public static State? Read(string dataDir)
    {
        var p = StatePath(dataDir);
        if (!File.Exists(p)) return null;
        try { return System.Text.Json.JsonSerializer.Deserialize<State>(File.ReadAllText(p), Wire.Options); }
        catch (System.Text.Json.JsonException) { return null; }
    }

    /// <summary>Swap to the given image, remembering the pre-downed wallpaper exactly once.</summary>
    public static void Apply(string imageFile, string dataDir, string? contentId)
    {
        var st = Read(dataDir) ?? new State(Baseline: CurrentOnMachine(dataDir), Current: null, ContentId: null);
        SetOnMachine(imageFile);
        Write(dataDir, st with { Current = imageFile, ContentId = contentId });
    }

    /// <summary>Return to the baseline captured when downed was first applied (from the machine_state record).</summary>
    public static void Restore(string dataDir)
    {
        var st = Read(dataDir);
        if (st is null) return;
        if (st.Baseline is { Length: > 0 } baseFile && File.Exists(baseFile)) SetOnMachine(baseFile);
        else if (OperatingSystem.IsWindows()) SetOnMachineWindows(null);   // empty string clears
        Write(dataDir, st with { Current = null, ContentId = null });
    }

    private static void Write(string dataDir, State st) =>
        File.WriteAllText(StatePath(dataDir), System.Text.Json.JsonSerializer.Serialize(st, Wire.Options));

    public static string? CurrentOnMachine(string dataDir)
    {
        if (OperatingSystem.IsWindows()) return CurrentWallpaperWindows();
        return Read(dataDir)?.Current;   // dev surface: the state file is the only "current" truth
    }

    private static void SetOnMachine(string imageFile)
    {
        if (OperatingSystem.IsWindows()) SetOnMachineWindows(imageFile);
        // Linux dev surface: the state file (written by Apply/Restore) is the observable effect.
    }

    // ---- Windows implementation: SPI_SETDESKWALLPAPER (Unicode), broadcast WM_SETTINGCHANGE ----
    [SupportedOSPlatform("windows")]
    private static void SetOnMachineWindows(string? imageFile)
    {
        // SPI_SETDESKWALLPAPER=0x0014; flags SPIF_UPDATEINIFILE|SPIF_SENDCHANGE = 0x01|0x02
        SystemParametersInfo(0x0014, 0, imageFile ?? "", 0x03);
    }

    [SupportedOSPlatform("windows")]
    private static string? CurrentWallpaperWindows()
    {
        using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
        return k?.GetValue("WallPaper") as string;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, string pvParam, uint fWinIni);
}

/// <summary>Wallpaper drift watchdog — the convergence loop applies the desired image, but the
/// desktop can drift afterwards (the user changes the wallpaper, the applied file is tampered with).
/// The agent loop evaluates drift every beat: hash the file the machine currently references
/// (Windows: Control Panel\Desktop WallPaper path; Linux dev surface: wallpaper-state.json Current)
/// against the sha recorded when we applied the target. No target → no evaluation: when nothing is
/// enforced the user owns the desktop. Node fields stay stable between flips so the report-hash
/// dedupe is not disturbed; the flip itself changes the hash and the loop forces a prompt report.</summary>
public static class WallpaperWatch
{
    public sealed record Sync(bool Drift, string? ExpectedSha256, string? ActualSha256)
    {
        public System.Text.Json.Nodes.JsonNode ToNode() => new System.Text.Json.Nodes.JsonObject
        {
            ["drift"] = Drift,
            ["expected_sha256"] = ExpectedSha256,
            ["actual_sha256"] = ActualSha256,
        };
    }

    private static volatile string? _expected;   // sha of the target currently enforced (null = unmanaged)

    public static string? ExpectedSha256 => _expected;
    public static Sync? Last { get; private set; }
    public static void SetExpected(string? sha) { _expected = sha; if (sha is null) Last = null; }
    public static void Reset() { _expected = null; Last = null; }

    public static Sync Check(string dataDir)
    {
        var p = Wallpaper.CurrentOnMachine(dataDir);
        string? actual = null;
        if (p is { Length: > 0 } && File.Exists(p))
            try { actual = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p))).ToLowerInvariant(); }
            catch (IOException) { /* mid-write or locked by a preview — treat as unreadable this beat */ }
        var s = new Sync(_expected is not null && actual != _expected, _expected, actual);
        Last = s;
        return s;
    }
}
