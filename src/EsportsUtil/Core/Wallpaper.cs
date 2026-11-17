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

    private static string? CurrentOnMachine(string dataDir)
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
