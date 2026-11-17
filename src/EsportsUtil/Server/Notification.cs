using System.Text;
#if WINDOWS
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
#endif
using EsportsUtil.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EsportsUtil.Server;

/// <summary>Phase 3 — asynchronous alerting: ingestion (API handlers, watchdogs) only enqueues to the
/// AlertHub channel; the hosted NotificationService drains it and hands each alert to an INotifier.
/// On the Windows server box the notifier is a real toast; on dev/Linux it is the console. A notifier
/// failure never stops the queue and never crashes ingestion.</summary>
public interface INotifier
{
    Task NotifyAsync(Alert a, CancellationToken ct);
}

public sealed class ConsoleNotifier : INotifier
{
    public Task NotifyAsync(Alert a, CancellationToken ct)
    {
        Console.WriteLine($"[alert] {a.Severity} {a.Type} {a.ClientId} ({a.DevicePin}): {a.Message}");
        return Task.CompletedTask;
    }
}

/// <summary>Toast XML builder — shared and pure so it is unit-testable on any OS.</summary>
public static class ToastXml
{
    static string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    public static string Build(Alert a, string dashboardUrl)
    {
        var critical = a.Severity == "critical";
        var title = $"E-Sports: {a.Type.Replace('_', ' ')}";
        var who = a.DevicePin is not null ? $"#{a.DevicePin}" : a.ClientId;
        var body = who is null ? a.Message : $"{who} — {a.Message}";
        var attrs = critical ? " scenario=\"immediate\"" : " duration=\"short\"";
        return $"<toast{attrs}>" +
               $"<visual><binding template=\"ToastGeneric\"><text>{Esc(title)}</text><text>{Esc(body)}</text></binding></visual>" +
               $"<audio src=\"ms-winsoundevent:Notification.{(critical ? "IM" : "Default")}\"/>" +
               $"<actions><action activationType=\"uri\" arguments=\"{Uri.EscapeDataString(dashboardUrl)}\" content=\"Open dashboard\"/></actions>" +
               $"</toast>";
    }
}

/// <summary>Hosted notification daemon (PLAN.md Phase 3): drains the in-process queue, never blocks ingestion.</summary>
public sealed class NotificationService(AlertHub hub, INotifier notifier, ILogger log) : BackgroundService
{
    private static readonly ConsoleNotifier Fallback = new();

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await foreach (var a in hub.Reader.ReadAllAsync(ct))
        {
            try
            {
                await notifier.NotifyAsync(a, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                log.LogWarning("notifier failed for {type} ({msg}) — falling back to console: {err}", a.Type, a.Message, ex.Message);
                try { await Fallback.NotifyAsync(a, ct); } catch { /* console failure is terminal-silent; queue keeps draining */ }
            }
        }
    }
}

#if WINDOWS
/// <summary>Windows toast via WinRT (Windows.UI.Notifications). A classic Win32 process must present a
/// registered AppUserModelID backed by a Start-Menu shortcut, otherwise Windows silently drops toasts —
/// so first-run sets the process AUMID and writes a per-user shortcut (identity only; double-click also
/// serves as a server launcher).</summary>
[SupportedOSPlatform("windows10.0.19041")]
public sealed class WindowsToastNotifier(string dashboardUrl, string aumid = "EsportsUtil.Server") : INotifier
{
    private bool _ready;

    private void EnsureReady()
    {
        if (_ready) return;
        Win32.SetCurrentProcessExplicitAppUserModelID(aumid);
        ShellLink.EnsureStartMenuShortcut(aumid);
        _ready = true;
    }

    public Task NotifyAsync(Alert a, CancellationToken ct)
    {
        EnsureReady();
        var doc = new Windows.Data.Xml.Dom.XmlDocument();
        doc.LoadXml(ToastXml.Build(a, dashboardUrl));
        var toast = new Windows.UI.Notifications.ToastNotification(doc)
        {
            Group = "esportsutil",
            ExpirationTime = DateTimeOffset.UtcNow + (a.Severity == "critical" ? TimeSpan.FromHours(4) : TimeSpan.FromMinutes(30)),
        };
        // "immediate" scenario (bypasses quiet hours) comes from the toast XML attribute set by ToastXml.Build.
        Windows.UI.Notifications.ToastNotificationManager.CreateToastNotifier().Show(toast);
        return Task.CompletedTask;
    }
}

static class Win32
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int SetCurrentProcessExplicitAppUserModelID(string AppID);
}

static class ShellLink
{
    const uint STGM = 0x20;   // STGM_SHARE_EXCLUSIVE... Save(fRemember, permanent) handles it

    public static void EnsureStartMenuShortcut(string aumid)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "EsportsUtil");
        Directory.CreateDirectory(dir);
        var lnk = Path.Combine(dir, "EsportsUtil Server.lnk");
        if (File.Exists(lnk)) return;

        var exe = Environment.ProcessPath ?? Environment.GetCommandLineArgs()[0];
        var link = (IShellLinkW)Activator.CreateInstance(
            Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"), throwOnError: true)!)!;
        if (link.SetPath(exe) != 0) throw new InvalidOperationException("shell link SetPath failed");
        if (link.SetDescription("EsportsUtil — E-Sports Dashboard (server)") != 0) throw new InvalidOperationException("shell link SetDescription failed");

        var store = (IPropertyStore)link;  // RCW QueryInterface — shell links expose IPropertyStore directly
        var key = new PropertyKey { fmtid = new Guid("9F0C8163-BE48-4BA9-8C22-4AE4E2F1B743"), pid = 5 }; // PKEY_AppUserModel_ID
        var pv = new PROPVARIANT();
        try
        {
            if (PropVariantFromString(aumid, out pv) != 0) throw new InvalidOperationException("PropVariantFromString failed");
            if (store.Set(ref key, ref pv) != 0) throw new InvalidOperationException("IPropertyStore.Set failed");
            if (store.CommitChanges() != 0) throw new InvalidOperationException("IPropertyStore.CommitChanges failed");
        }
        finally { PropVariantClear(ref pv); }

        if (((IPersistFile)link).Save(lnk, true) != 0) throw new InvalidOperationException("shortcut Save failed");
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PropertyKey { public Guid fmtid; public int pid; }

    [StructLayout(LayoutKind.Sequential)]
    struct PROPVARIANT
    {
        public ushort vt;
        public ushort r1, r2, r3;
        public IntPtr p1, p2;
    }

    [DllImport("propsys.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern int PropVariantFromString(string psz, out PROPVARIANT pv);

    [DllImport("ole32.dll", SetLastError = true)]
    static extern int PropVariantClear(ref PROPVARIANT pv);

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellLinkW
    {
        [PreserveSig] int GetPath(StringBuilder pszFile, int cchMax, IntPtr pfd, uint fFlags);
        [PreserveSig] int GetIDList(out IntPtr pidl);
        [PreserveSig] int SetIDList(IntPtr pidl);
        [PreserveSig] int GetDescription(StringBuilder pszName, int cchMax);
        [PreserveSig] int SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        [PreserveSig] int GetWorkingDirectory(StringBuilder pszDir, int cchMax);
        [PreserveSig] int SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        [PreserveSig] int GetArguments(StringBuilder pszArgs, int cchMax);
        [PreserveSig] int SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        [PreserveSig] int GetShowCmd(out int piShowCmd);
        [PreserveSig] int SetShowCmd(int iShowCmd);
        [PreserveSig] int GetIconLocation(StringBuilder pszIconPath, int cchIconPath, out int piIconIndex);
        [PreserveSig] int SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIconIndex);
        [PreserveSig] int SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        [PreserveSig] int Resolve(IntPtr hwnd, uint fFlags);
        [PreserveSig] int SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport, Guid("8856F961-340A-11D0-962E-00A0C912B6D1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out int cProps);
        [PreserveSig] int GetAt(int iProp, out PropertyKey pKey);
        [PreserveSig] int Get(ref PropertyKey key, IntPtr pv);
        [PreserveSig] int Set(ref PropertyKey key, ref PROPVARIANT pv);
        [PreserveSig] int Remove(ref PropertyKey key);
        [PreserveSig] int CommitChanges();
    }

    [ComImport, Guid("0000010B-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPersistFile
    {
        [PreserveSig] int GetRemote(out IntPtr ppszFileName);
        [PreserveSig] int Load([MarshalAs(UnmanagedType.LPWStr)] string pszFilename, uint fMode);
        [PreserveSig] int Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        [PreserveSig] int SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        [PreserveSig] int GetCurFile(out IntPtr ppszFileName);
    }
}
#endif
