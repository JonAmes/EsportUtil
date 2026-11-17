using System.Diagnostics;
using EsportsUtil.Core;

namespace EsportsUtil.Agent;

/// <summary>Spawns the locate overlay as a same-EXE child (`esportsutil --overlay …`) so the render
/// happens in the interactive session, transient and self-dismissing — zero steady-state cost.
/// The agent itself never paints UI; it only stamps the request and launches (PLAN.md Phase 4 locate).</summary>
public static class OverlayLauncher
{
    /// <summary>Fire-and-forget. Returns false when spawn failed (logged, never crashes the loop).</summary>
    public static bool Launch(LocateOverlayRequest req)
    {
        var exe = Environment.ProcessPath ?? Environment.GetCommandLineArgs()[0];
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                CreateNoWindow = OperatingSystem.IsWindows(),   // no console flash behind the overlay
                RedirectStandardOutput = true,                  // child chatter never pollutes the agent loop
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var a in LocateOverlayRequest.BuildArgs(req)) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            return p is not null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Console.WriteLine($"locate overlay spawn failed: {e.Message}");
            return false;
        }
    }

    public static LocateOverlayRequest RequestFor(Directive d, AgentIdentity id, string? model) =>
        new(d.Payload is { Length: > 0 } p ? p : $"PIN {id.DevicePin}", id.DevicePin, id.ClientId, d.ExpiresUnix, model);
}
