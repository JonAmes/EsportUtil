#if WINDOWS
using System.Diagnostics;
using System.Runtime.Versioning;
using System.ServiceProcess;
using Microsoft.Win32;

namespace EsportsUtil.Core;

/// <summary>Windows process-lifecycle ops: install/remove/control the Windows service via sc.exe +
/// ServiceController (never PowerShell), and tray autostart via the HKCU Run key. Every argument
/// string is built by the pure Lifecycle builders so this file only executes data.</summary>
[SupportedOSPlatform("windows10.0.19041")]
public static class ServiceOps
{
    public static async Task<int> RunAsync(string verb, string? sub, string name, string? configPath)
    {
        if (!Lifecycle.KnownSubVerb(verb, sub))
        {
            Console.Error.WriteLine(verb == "service"
                ? "usage: esportsutil service install|remove|start|stop|status [--name NAME] [--config PATH]"
                : "usage: esportsutil autostart enable|disable|status");
            return 2;
        }
        var exe = Environment.ProcessPath
            ?? Process.GetCurrentProcess().MainModule?.FileName
            ?? throw new InvalidOperationException("cannot resolve own exe path");

        if (verb == "autostart") return Autostart(sub!, exe);

        switch (sub!)
        {
            case "install":
                if (ServiceExists(name))
                {
                    Console.Error.WriteLine($"service '{name}' is already installed — 'esportsutil service remove' first to reinstall");
                    return 1;
                }
                var rc = await RunScAsync(Lifecycle.ScCreateArgs(name, exe, configPath));
                if (rc != 0) return rc;
                await RunScAsync(Lifecycle.ScDescriptionArgs(name));
                await RunScAsync(Lifecycle.ScFailureArgs(name));
                Console.WriteLine($"service '{name}' installed — auto-start, crash-restart 5s/30s/60s.");
                Console.WriteLine("start it now:   esportsutil service start");
                Console.WriteLine("NOTE: a service runs in session 0 — locate/downed overlays need a logged-in session.");
                Console.WriteLine("      on game PCs prefer tray mode:  esportsutil autostart enable   (runs --tray in the user session)");
                return 0;

            case "remove":
                if (ServiceExists(name))
                    try { StopQuietly(name); } catch (InvalidOperationException) { }
                return await RunScAsync(Lifecycle.ScDeleteArgs(name));

            case "start":
                return ControllerVerb(name, c => { c.Start(); Console.WriteLine($"starting '{name}'…"); return 0; });

            case "stop":
                return ControllerVerb(name, c => { c.Stop(); Console.WriteLine($"stopping '{name}'…"); return 0; });

            default: // status
                if (!ServiceExists(name)) { Console.WriteLine($"service '{name}' is not installed"); return 1; }
                var sc = new ServiceController(name);
                sc.Refresh();
                Console.WriteLine($"'{name}': {sc.Status}");
                return 0;
        }
    }

    private static bool ServiceExists(string name) =>
        ServiceController.GetServices().Any(s => string.Equals(s.ServiceName, name, StringComparison.OrdinalIgnoreCase));

    private static void StopQuietly(string name)
    {
        using var c = new ServiceController(name);
        if (c.Status != ServiceControllerStatus.Stopped) c.Stop();
    }

    private static int ControllerVerb(string name, Func<ServiceController, int> act)
    {
        if (!ServiceExists(name)) { Console.Error.WriteLine($"service '{name}' is not installed"); return 1; }
        using var c = new ServiceController(name);
        return act(c);
    }

    private static async Task<int> RunScAsync(List<string> argv)
    {
        var psi = new ProcessStartInfo("sc.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in argv) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);
        if (p is null) { Console.Error.WriteLine("could not start sc.exe"); return 1; }
        var so = await p.StandardOutput.ReadToEndAsync();
        var se = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        foreach (var line in (so + se).Split('\n'))
            if (!string.IsNullOrWhiteSpace(line)) Console.WriteLine(line.TrimEnd());
        if (p.ExitCode != 0) Console.Error.WriteLine($"sc {argv[0]} failed (exit {p.ExitCode}) — an elevated (Administrator) shell is required for service install/remove.");
        return p.ExitCode;
    }

    private static int Autostart(string sub, string exe)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Lifecycle.RunKeyPath, writable: sub != "status");
        if (key is null) { Console.Error.WriteLine("cannot open HKCU Run key"); return 1; }
        switch (sub)
        {
            case "enable":
                key.SetValue(Lifecycle.RunValueName, Lifecycle.RunKeyValue(exe));
                Console.WriteLine($"tray autostart enabled: {Lifecycle.RunValueName} = {Lifecycle.RunKeyValue(exe)}");
                Console.WriteLine("(takes effect at next sign-in; sign out/in, or launch `esportsutil --tray` once by hand now)");
                return 0;
            case "disable":
                if (key.GetValue(Lifecycle.RunValueName) is null) { Console.WriteLine("tray autostart is not enabled"); return 0; }
                key.DeleteValue(Lifecycle.RunValueName);
                Console.WriteLine("tray autostart disabled");
                return 0;
            default:
                var v = key.GetValue(Lifecycle.RunValueName) as string;
                Console.WriteLine(v is null ? "tray autostart: disabled (no HKCU Run entry)" : $"tray autostart: enabled — {v}");
                return 0;
        }
    }
}

/// <summary>ServiceBase host for the internal --service mode: OnStart drives the shared RoleRunner
/// on a background task, OnStop cancels and drains it so SCM gets a clean stop.</summary>
[SupportedOSPlatform("windows10.0.19041")]
internal sealed class EsportsUtilService : ServiceBase
{
    private readonly EsportsConfig _cfg;
    private CancellationTokenSource? _cts;
    private Task _run = Task.CompletedTask;

    public EsportsUtilService(EsportsConfig cfg)
    {
        _cfg = cfg;
        CanStop = true;
        CanShutdown = true;
    }

    protected override void OnStart(string[] args)
    {
        _cts = new CancellationTokenSource();
        _run = Task.Run(() => RoleRunner.RunAsync(_cfg, _cts.Token));
    }

    protected override void OnStop()
    {
        _cts?.Cancel();
        try { _run.Wait(TimeSpan.FromSeconds(15)); }
        catch (Exception) { }
    }
}
#endif
