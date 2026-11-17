namespace EsportsUtil.Core;

/// <summary>How the same EXE hosts its role loop: console foreground, tray in the user session,
/// or Windows-service (session 0). Mode decides UI placement only — RoleRunner is identical.</summary>
public enum RunMode { Foreground, Tray, Service }

public sealed record LifecycleCommand(RunMode Mode, string? Verb, string? SubVerb, string ServiceName)
{
    public bool IsOpsVerb => Verb is not null;
}

/// <summary>Pure lifecycle parsing + command construction (sc.exe argv, HKCU Run value, console
/// teeing). Everything the Windows ops surfaces execute is built here as data so the exact
/// strings are unit-testable on the Linux dev box (no-duplication rule agains the ops surfaces).</summary>
public static class Lifecycle
{
    public const string DefaultServiceName = "EsportsUtil";
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string RunValueName = "EsportsUtil";

    public static LifecycleCommand Parse(IReadOnlyList<string> args, bool trayFlag, bool serviceFlag, string? nameFlag)
    {
        string? verb = null, sub = null;
        if (args.Count > 0 && args[0] is "service" or "autostart")
        {
            verb = args[0];
            sub = args.Count > 1 ? args[1] : null;
        }
        var mode = serviceFlag ? RunMode.Service : trayFlag ? RunMode.Tray : RunMode.Foreground;
        var name = string.IsNullOrWhiteSpace(nameFlag) ? DefaultServiceName : nameFlag!;
        return new LifecycleCommand(mode, verb, sub, name);
    }

    public static bool KnownSubVerb(string verb, string? sub) =>
        sub is not null && (verb == "service"
            ? sub is "install" or "remove" or "start" or "stop" or "status"
            : sub is "enable" or "disable" or "status");

    // ---- sc.exe argument builders (pure; these exact strings ARE the contract) ----

    /// <summary>binPath value: quoted own-exe + the internal --service entry flag + optional config.</summary>
    public static string ServiceBinPath(string exePath, string? configPath) =>
        $"\"{exePath}\" --service" + (string.IsNullOrWhiteSpace(configPath) ? "" : $" --config \"{configPath}\"");

    public static List<string> ScCreateArgs(string name, string exePath, string? configPath) =>
        new() { "create", name, "binPath=", ServiceBinPath(exePath, configPath), "start=", "auto" };

    public static List<string> ScDescriptionArgs(string name) =>
        new() { "description", name, "\"EsportsUtil venue fleet role (server or agent) — installed by: esportsutil service install\"" };

    public static List<string> ScFailureArgs(string name) =>
        new() { "failure", name, "reset=", "86400", "actions=", "restart/5000/restart/30000/restart/60000" };

    public static List<string> ScDeleteArgs(string name) => new() { "delete", name };

    /// <summary>HKCU Run value — quotes the exe path (Program Files spaces) and pins tray mode.</summary>
    public static string RunKeyValue(string exePath) => $"\"{exePath}\" --tray";

    public static string LogPath(string dataDir) => Path.Combine(dataDir, "esportsutil.log");

    /// <summary>Tray/service modes have no console an operator reads — tee stdout/stderr to a log file.</summary>
    public static void TeeConsoleToLog(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        var w = new StreamWriter(new FileStream(LogPath(dataDir), FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        { AutoFlush = true };
        Console.SetOut(w);
        Console.SetError(w);
    }
}

/// <summary>Tray link-state decision: agent-loop projection in, (key, color, tooltip) out. Pure and
/// Linux-testable; the WinForms tray only paints the dot and sets the text it is given.</summary>
public static class TrayStatus
{
    public static (string Key, string Color, string Tip) ForAgent(
        bool acked, bool lastAckOk, string? pairedName, string? pin, string desiredState)
    {
        if (!acked) return ("starting", "gray", "EsportsUtil agent — starting");
        if (!lastAckOk) return ("offline", "red", $"EsportsUtil agent — server unreachable · PIN {pin}");
        if (pairedName is null) return ("pairing", "amber", $"EsportsUtil agent — awaiting pairing · PIN {pin}");
        if (desiredState == "downed") return ("downed", "amber", $"EsportsUtil — {pairedName} · DOWNED");
        return ("linked", "green", $"EsportsUtil — {pairedName} linked · PIN {pin}");
    }

    public static (string Key, string Color, string Tip) ForServer(string url) =>
        ("server", "green", $"EsportsUtil server — {url}");
}
