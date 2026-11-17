using System.Diagnostics;
using EsportsUtil.Core;

namespace EsportsUtil.Agent;

/// <summary>Spawns the locate overlay as a same-EXE child (`esportsutil --overlay …`) so the render
/// happens in the interactive session, transient and self-dismissing — zero steady-state cost.
/// The agent itself never paints UI; it only stamps the request and launches (PLAN.md Phase 4 locate).</summary>
public static class OverlayLauncher
{
    /// <summary>Common child-proc shape for same-EXE overlay modes. NOTE: when the agent itself runs
    /// as `dotnet esportsutil.dll` (dev/Linux), ProcessPath is the dotnet host — we re-inject the
    /// managed entrypoint as the first arg so the child runs the same program, not bare `dotnet`.
    /// Redirected pipes are always drained (see DrainPipes) so a chatty console card can never block
    /// the child on a full pipe buffer.</summary>
    internal static ProcessStartInfo OverlayStart()
    {
        var psi = new ProcessStartInfo
        {
            FileName = Environment.ProcessPath ?? Environment.GetCommandLineArgs()[0],
            CreateNoWindow = OperatingSystem.IsWindows(),   // no console flash behind the overlay
            RedirectStandardOutput = true,                  // child chatter never pollutes the agent loop
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        var entry = Environment.GetCommandLineArgs()[0];
        if (!string.Equals(psi.FileName, entry, StringComparison.OrdinalIgnoreCase))
            psi.ArgumentList.Add(entry);
        return psi;
    }

    internal static void DrainPipes(Process p)
    {
        p.OutputDataReceived += (_, _) => { };
        p.ErrorDataReceived += (_, _) => { };
        try { p.BeginOutputReadLine(); p.BeginErrorReadLine(); } catch (InvalidOperationException) { }
    }

    /// <summary>Fire-and-forget. Returns false when spawn failed (logged, never crashes the loop).</summary>
    public static bool Launch(LocateOverlayRequest req)
    {
        try
        {
            var psi = OverlayStart();
            foreach (var a in LocateOverlayRequest.BuildArgs(req)) psi.ArgumentList.Add(a);
            var p = Process.Start(psi);
            if (p is null) return false;
            DrainPipes(p);          // child lives on; pipes must drain, not just redirect
            return true;
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

/// <summary>Lifetime handle for the supervised sticky downed overlay child: the agent loop polls
/// Alive each ack (a staff bypass on the machine = child exited) and Kill()s the child when the
/// operator returns the machine to active.</summary>
public interface IDownOverlayHandle
{
    bool Alive { get; }
    void Kill();
}

/// <summary>Supervised variant of Launch for the no-expiry downed overlay: same same-EXE child, but
/// the Process handle is returned instead of disposed, so the loop can watch and stop it (Phase 5).</summary>
public static class DownOverlayLauncher
{
    public static IDownOverlayHandle? Launch(DownedOverlayRequest req)
    {
        try
        {
            var psi = OverlayLauncher.OverlayStart();
            foreach (var a in DownedOverlayRequest.BuildArgs(req)) psi.ArgumentList.Add(a);
            var p = Process.Start(psi);
            if (p is null) return null;
            OverlayLauncher.DrainPipes(p);
            return new ProcessHandle(p);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Console.WriteLine($"downed overlay spawn failed: {e.Message}");
            return null;
        }
    }

    private sealed class ProcessHandle : IDownOverlayHandle
    {
        private readonly Process _p;
        public ProcessHandle(Process p) => _p = p;
        public bool Alive { get { try { return !_p.HasExited; } catch { return false; } } }
        public void Kill()
        {
            try { if (!_p.HasExited) _p.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }           // already exited between check and kill
            finally { _p.Dispose(); }
        }
    }
}
