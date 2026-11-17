#if WINDOWS
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows.Forms;
using EsportsUtil.Agent;

namespace EsportsUtil.Core;

/// <summary>System-tray mode: the full agent/server role runs on a background task while a
/// NotifyIcon carries live link state (dot color + tooltip come from the pure TrayStatus map).
/// The console window a Run-key launch creates is hidden; output is teed to the log file.
/// Menu mirrors what an operator needs standing at the machine: identity/PIN, state, dashboard, Exit.</summary>
[SupportedOSPlatform("windows10.0.19041")]
public static class TrayApp
{
    [DllImport("kernel32.dll")] private static extern nint GetConsoleWindow();
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint handle);

    public static int Run(EsportsConfig cfg)
    {
        var hwnd = GetConsoleWindow();
        if (hwnd != 0) ShowWindow(hwnd, 0);   // SW_HIDE — the Run-key console flashes away instantly
        Lifecycle.TeeConsoleToLog(cfg.DataDir);
        using var cts = new CancellationTokenSource();
        using var ctx = new TrayContext(cfg, cts);
        Application.Run(ctx);
        cts.Cancel();                          // safety net: Exit already canceled; double-cancel is a no-op
        return 0;
    }

    private sealed class TrayContext : ApplicationContext
    {
        private readonly NotifyIcon _icon;
        private readonly CancellationTokenSource _cts;
        private readonly EsportsConfig _cfg;
        private readonly ToolStripMenuItem _idItem, _stateItem, _openItem;
        private readonly System.Windows.Forms.Timer _timer;
        private readonly Dictionary<string, Icon> _icons = new();
        private AgentLoop? _loop;
        private string? _lastIconKey;

        public TrayContext(EsportsConfig cfg, CancellationTokenSource cts)
        {
            _cfg = cfg;
            _cts = cts;
            _icon = new NotifyIcon { Visible = true, Text = "EsportsUtil" };

            var menu = new ContextMenuStrip();
            _idItem = new ToolStripMenuItem(cfg.Role == "server" ? "venue server" : "awaiting identity…") { Enabled = false };
            _stateItem = new ToolStripMenuItem("starting…") { Enabled = false };
            _openItem = new ToolStripMenuItem(cfg.Role == "server" ? "Open server dashboard" : "Open client dashboard");
            _openItem.Click += (_, _) => OpenDashboard();
            var exitItem = new ToolStripMenuItem("Exit");
            exitItem.Click += (_, _) => { _cts.Cancel(); ExitThread(); };
            menu.Items.AddRange([_idItem, _stateItem, new ToolStripSeparator(), _openItem, exitItem]);
            _icon.ContextMenuStrip = menu;
            _icon.DoubleClick += (_, _) => OpenDashboard();
            SetVisual("starting", "gray", "EsportsUtil");

            _timer = new System.Windows.Forms.Timer { Interval = 2000 };
            _timer.Tick += (_, _) => RefreshStatus();
            _timer.Start();

            // The role loop is the SAME object graph as console foreground (RoleRunner); the tray
            // only reads its live projection. Fire-and-forget: RunAsync owns its failure handling.
            _ = Task.Run(() => RoleRunner.RunAsync(_cfg, _cts.Token, loop => _loop = loop));
        }

        private void RefreshStatus()
        {
            var st = _cfg.Role == "server"
                ? TrayStatus.ForServer(_cfg.ServerUrl)
                : TrayStatus.ForAgent(
                    _loop?.LastAckAt is not null,
                    _loop?.LastAckOk == true,
                    _loop?.PairedDisplayName,
                    _loop?.CurrentIdentity?.DevicePin,
                    _loop?.LastDesiredState ?? "active");

            if (st.Key != _lastIconKey) SetVisual(st.Key, st.Color, st.Tip);
            else _icon.Text = Tip63(st.Tip);

            _stateItem.Text = st.Key switch
            {
                "starting" => "starting…",
                "offline" => "server unreachable",
                "pairing" => "awaiting operator pairing",
                "downed" => "DOWNED (out of service)",
                "linked" => "linked — reporting",
                _ => "running",
            };
            if (_cfg.Role != "server" && _loop?.CurrentIdentity is { } id)
                _idItem.Text = $"{_loop.PairedDisplayName ?? "(unpaired)"} · PIN {id.DevicePin}";
        }

        private static string Tip63(string tip) => tip.Length <= 63 ? tip : tip[..63]; // legacy NotifyIcon.Text hard cap

        private void SetVisual(string key, string color, string tip)
        {
            _lastIconKey = key;
            _icon.Icon = IconFor(color);
            _icon.Text = Tip63(tip);
        }

        /// <summary>Status dot painted at runtime — no .ico asset to ship or version.</summary>
        private Icon IconFor(string colorName)
        {
            if (_icons.TryGetValue(colorName, out var cached)) return cached;
            var color = colorName switch
            {
                "green" => Color.FromArgb(0x16, 0xa3, 0x4a),
                "amber" => Color.FromArgb(0xd9, 0x77, 0x06),
                "red" => Color.FromArgb(0xdc, 0x26, 0x26),
                _ => Color.FromArgb(0x9c, 0xa3, 0xaf),
            };
            using var bmp = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using var b = new SolidBrush(color);
                g.FillEllipse(b, 2, 2, 11, 11);
                g.DrawEllipse(Pens.Black, 2, 2, 11, 11);
            }
            var h = bmp.GetHicon();
            try
            {
                using var tmp = Icon.FromHandle(h);
                var clone = (Icon)tmp.Clone();
                _icons[colorName] = clone;
                return clone;
            }
            finally { DestroyIcon(h); }
        }

        private void OpenDashboard()
        {
            var url = _cfg.Role == "server"
                ? $"http://127.0.0.1:{_cfg.Port}/"
                : $"http://127.0.0.1:{_cfg.AgentDashboardPort}/";
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception) { }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _timer.Dispose();
                _icon.Dispose();
                foreach (var ic in _icons.Values) ic.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
#endif
