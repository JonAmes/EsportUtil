using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using System.Text.Json;
using Microsoft.Win32;

namespace EsportsUtil.Core;

/// <summary>State Module Record — the MachineState.ps1 engine's JSON shape ported to C#, emitted
/// verbatim into the Local Manifest's machine_state (single owner; the agent transport never re-implements capture).
/// Windows sources: registry BIOS keys for model, Uninstall keys for the full software inventory,
/// Run keys + StartupApproved for startup items, Control Panel Desktop for wallpaper/screensaver.
/// The dev box (Linux) fills machine identity from DMI so the record shape is identical everywhere;
/// Windows-only sections come back as empty arrays, never missing keys.</summary>
public static class StateModule
{
    public static JsonObject Record()
    {
        var (vendor, model) = MachineIdentity();
        var installedApps = InstalledAppsOrFake();
        return new JsonObject
        {
            ["computerName"] = Environment.MachineName,
            ["machineManufacturer"] = vendor,
            ["machineModel"] = model,
            ["captured_timestamp"] = DateTimeOffset.UtcNow.ToString("O"),
            ["os"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            ["displays"] = new JsonArray(),          // populated on Windows (ChangeDisplaySettingsEx read side)
            ["startupItems"] = OperatingSystem.IsWindows() ? ReadStartupItems() : new JsonArray(),
            ["screensaver"] = OperatingSystem.IsWindows() ? ReadScreensaver() : null,
            ["wallpaper"] = OperatingSystem.IsWindows() ? ReadWallpaper() : null,
            ["installedApps"] = installedApps,
            // Venue-app install/up-to-date verdicts (T29 catalog): one row per installable app.
            ["apps"] = AppCatalog.ToNode(AppCatalog.Statuses(installedApps, AppVersionCache.LatestOf,
                AppVersionCache.FetchedUnix)),
            // Last Windows Update poll outcome (agent loop owns the cadence; the record embeds the
            // cache). Key absent until the first poll — the server pill then stays plain green.
            ["windowsUpdate"] = UpdateWatch.Last?.ToNode(),
            // Wallpaper drift verdict vs the enforced target (agent loop checks every beat). Key
            // absent while nothing is enforced — the user owns the desktop then.
            ["wallpaperSync"] = WallpaperWatch.Last?.ToNode(),
            // SteamCMD presence + winget bootstrap verdict (agent loop owns the cadence).
            ["steamCmd"] = SteamCmdWatch.Last?.ToNode(),
            ["selfUpdate"] = SelfUpdateWatch.Last?.ToNode(),
        };
    }

    private static (string? Vendor, string? Model) MachineIdentity()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var bios = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
                return (bios?.GetValue("SystemManufacturer") as string,
                        bios?.GetValue("SystemProductName") as string);
            }
            catch (Exception) { return (null, null); }
        }
        string? Read(string p)
        {
            try { return File.Exists(p) ? File.ReadAllText(p).Trim() : null; }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }
        return (Read("/sys/class/dmi/id/sys_vendor"), Read("/sys/class/dmi/id/model_name") ?? Read("/sys/class/dmi/id/product_name"));
    }

    [SupportedOSPlatform("windows")]
    private static JsonNode? ReadWallpaper()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            var v = k?.GetValue("WallPaper") as string;
            return string.IsNullOrEmpty(v) ? null : JsonValue.Create(v);
        }
        catch { return null; }
    }

    [SupportedOSPlatform("windows")]
    private static JsonNode? ReadScreensaver()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            return new JsonObject
            {
                ["active"] = (k?.GetValue("SCRNSAVE.EXE") as string) is { Length: > 0 } on,
                ["executable"] = k?.GetValue("SCRNSAVE.EXE") as string,
                ["timeout_sec"] = int.TryParse(k?.GetValue("ScreenSaveTimeOut") as string, out var t) ? t : (int?)null,
            };
        }
        catch { return null; }
    }

    [SupportedOSPlatform("windows")]
    private static JsonArray ReadStartupItems()
    {
        var items = new JsonArray();
        foreach (var (hiveRoot, label) in new[] { (Registry.CurrentUser, "HKCU"), (Registry.LocalMachine, "HKLM") })
        {
            foreach (var runKey in new[] { @"Software\Microsoft\Windows\CurrentVersion\Run",
                                           @"Software\Microsoft\Windows\CurrentVersion\RunOnce" })
            {
                try
                {
                    using var k = hiveRoot.OpenSubKey(runKey);
                    if (k is null) continue;
                    var approvedKey = hiveRoot.OpenSubKey(
                        runKey.Replace(@"CurrentVersion\Run", @"Explorer\StartupApproved\Run"));
                    foreach (var name in k.GetValueNames())
                    {
                        items.Add(new JsonObject
                        {
                            ["hive"] = label,
                            ["key"] = runKey,
                            ["name"] = name,
                            ["command"] = k.GetValue(name) as string,
                            ["enabled"] = !IsStartupApprovedDisabled(approvedKey, name),
                        });
                    }
                    approvedKey?.Dispose();
                }
                catch { }
            }
        }
        return items;
    }

    [SupportedOSPlatform("windows")]
    private static bool IsStartupApprovedDisabled(RegistryKey? approvedKey, string name)
    {
        if (approvedKey?.GetValue(name) is byte[] blob && blob.Length >= 12)
            return blob[0] == 0x03 && blob[1] == 0x00;  // StartupApproved binary: [3,0,...] = disabled
        return false;
    }

    /// <summary>The installed-software inventory the agent reports. ESU_FAKE_INSTALLED_APPS holds a
    /// JSON array of installedApps-entry shape and overrides it for tests/demos (same seam style as
    /// ESU_FAKE_UPDATES); off Windows the real source doesn't exist, so empty unless faked.</summary>
    public const string FakeInstalledAppsEnvVar = "ESU_FAKE_INSTALLED_APPS";
    public static JsonArray InstalledAppsOrFake()
    {
        var fake = Environment.GetEnvironmentVariable(FakeInstalledAppsEnvVar);
        if (!string.IsNullOrWhiteSpace(fake))
        {
            try { return JsonNode.Parse(fake) as JsonArray ?? new JsonArray(); }
            catch (JsonException) { }
        }
        return OperatingSystem.IsWindows() ? ReadInstalledApps() : new JsonArray();
    }

    /// <summary>Full installed-software inventory: every Uninstall-key entry across FOUR registry
    /// views — HKLM 64-bit, HKLM 32-bit (= WOW6432Node, where Steam/Battle.net/Epic register),
    /// HKCU 64-bit (per-user Chrome etc.), HKCU 32-bit. A 64-bit process reading only the default
    /// view sees none of the 32-bit registrations — that was the invisible-apps bug (T31).
    /// (DisplayName, Publisher, DisplayVersion, InstallLocation, UninstallString, QuietUninstallString,
    /// hive, view.)</summary>
    static readonly (RegistryHive Hive, RegistryView View, string HiveName, string ViewName)[] UninstallViews =
    [
        (RegistryHive.LocalMachine, RegistryView.Registry64, "HKLM", "64"),
        (RegistryHive.LocalMachine, RegistryView.Registry32, "HKLM", "32"),
        (RegistryHive.CurrentUser, RegistryView.Registry64, "HKCU", "64"),
        (RegistryHive.CurrentUser, RegistryView.Registry32, "HKCU", "32"),
    ];
    public static string[] UninstallViewNames() => UninstallViews.Select(v => $"{v.HiveName}|{v.ViewName}").ToArray();

    [SupportedOSPlatform("windows")]
    private static JsonArray ReadInstalledApps()
    {
        var apps = new JsonArray();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (hive, view, hiveName, viewName) in UninstallViews)
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            var root = baseKey.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
            if (root is null) continue;
            foreach (var name in root.GetSubKeyNames())
            {
                try
                {
                    using var app = root.OpenSubKey(name);
                    var display = app?.GetValue("DisplayName") as string;
                    if (string.IsNullOrWhiteSpace(display)) continue;   // system components w/o display name
                    if (!seen.Add(display)) continue;
                    apps.Add(new JsonObject
                    {
                        ["registry_key"] = name,
                        ["display_name"] = display,
                        ["publisher"] = app?.GetValue("Publisher") as string,
                        ["display_version"] = app?.GetValue("DisplayVersion") as string,
                        ["install_location"] = app?.GetValue("InstallLocation") as string,
                        ["uninstall_string"] = app?.GetValue("UninstallString") as string,
                        ["quiet_uninstall_string"] = app?.GetValue("QuietUninstallString") as string,
                        ["hive"] = hiveName,
                        ["view"] = viewName,
                    });
                }
                catch { }
            }
        }
        return apps;
    }
}
