#if WINDOWS
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using EsportsUtil.Core;

namespace EsportsUtil.Agent;

/// <summary>Phase 4 locate overlay (PLAN.md): full-screen topmost window across the virtual desktop —
/// display_name, station metadata line, QR encoding the DevicePin (same payload as the printed
/// label), pulsing accent border, live auto-dismiss countdown. Closes on expiry, click, or ESC.
/// Foreground-of-everything guarantee: WM_WINDOWPOSCHANGING forces HWND_TOPMOST on every z-order
/// change (steals topmost back from any app that raises itself), each timer tick re-asserts it, and
/// first show tries SetForegroundWindow with a taskbar flash fallback when Windows' foreground lock
/// refuses the steal. True exclusive-fullscreen games (no DWM composition) remain the one case no
/// window can cover — venue games run borderless-windowed, which this does cover.
/// Runs on its own STA thread; the agent stays a console process.</summary>
[SupportedOSPlatform("windows10.0.19041")]
public static class LocateOverlayWindow
{
    public static void Run(LocateOverlayRequest req)
    {
        var t = new Thread(() => System.Windows.Forms.Application.Run(new OverlayForm(req)));
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
        t.Join();
    }

    private sealed class OverlayForm : System.Windows.Forms.Form
    {
        private static readonly Color Base = Color.FromArgb(12, 14, 20);      // near-black stage
        private static readonly Color Accent = Color.FromArgb(0, 200, 255);   // venue accent
        private readonly LocateOverlayRequest _req;
        private readonly System.Windows.Forms.Timer _timer = new() { Interval = 500 };
        private bool _pulse;

        public OverlayForm(LocateOverlayRequest req)
        {
            _req = req;
            Text = $"LOCATE — {req.DisplayName}";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = SystemInformation.VirtualScreen;    // span every monitor on the box
            TopMost = true;
            ShowInTaskbar = false;
            BackColor = Base;
            ForeColor = Color.White;
            DoubleBuffered = true;
            KeyPreview = true;
            KeyDown += (_, e) => { if (e.KeyCode == System.Windows.Forms.Keys.Escape) Close(); };
            MouseDown += (_, _) => Close();
            Shown += (_, _) =>
            {
                BringToFront();
                ForceTopmost();
                Activate();
                if (!SetForegroundWindow(Handle))          // foreground lock may refuse a background spawn
                {
                    var fi = new FLASHWINFO
                    {
                        cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<FLASHWINFO>(),
                        hwnd = Handle,
                        dwFlags = FLASHW_ALL | FLASHW_TIMERNOFG,
                        uCount = 3,
                        dwTimeout = 0,
                    };
                    FlashWindowEx(ref fi);
                }
                Focus();
            };
            _timer.Tick += (_, _) =>
            {
                _pulse = !_pulse;
                if (!_req.ShouldShow(DateTimeOffset.UtcNow.ToUnixTimeSeconds())) { Close(); return; }
                ForceTopmost();                           // reclaim z-order from anything that raised itself
                Invalidate();
            };
            _timer.Start();
        }

        // --- foreground-of-everything plumbing -----------------------------------------------
        private const int WM_WINDOWPOSCHANGING = 0x0046;
        private static readonly IntPtr HWND_TOPMOST = new(-1);
        private const uint SWP_NOMOVE = 0x0002, SWP_NOSIZE = 0x0001, SWP_NOACTIVATE = 0x0010;
        private const uint FLASHW_ALL = 0x0003, FLASHW_TIMERNOFG = 0x000C;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

#pragma warning disable CS0649 // fields are populated by Marshal.PtrToStructure, never directly
        private struct WINDOWPOS
        {
            public IntPtr hwnd, hwndInsertAfter;
            public int x, y, cx, cy;
            public uint flags;
        }
#pragma warning restore CS0649
        [StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct FLASHWINFO
        {
            public uint cbSize;
            public IntPtr hwnd;
            public uint dwFlags, uCount, dwTimeout;
        }

        private void ForceTopmost() =>
            SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_WINDOWPOSCHANGING)
            {
                // Force topmost on EVERY window-position/z-order change, so another TopMost app
                // (game HUD, screensaver, taskbar tricks) can never interleave above the overlay.
                var pos = System.Runtime.InteropServices.Marshal.PtrToStructure<WINDOWPOS>(m.LParam);
                pos.hwndInsertAfter = HWND_TOPMOST;
                System.Runtime.InteropServices.Marshal.StructureToPtr(pos, m.LParam, false);
            }
            base.WndProc(ref m);
        }

        protected override void OnFormClosed(System.Windows.Forms.FormClosedEventArgs e)
        {
            _timer.Dispose();
            base.OnFormClosed(e);
        }

        protected override void OnPaint(System.Windows.Forms.PaintEventArgs e)
        {
            var g = e.Graphics;
            var w = ClientSize.Width;
            var h = ClientSize.Height;

            // pulsing accent border
            using (var pen = new Pen(_pulse ? Accent : Color.FromArgb(70, 70, 90), 10))
                g.DrawRectangle(pen, 5, 5, w - 11, h - 11);

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var name = _req.DisplayName.ToUpperInvariant();
            var meta = _req.MetaLine();
            var payload = _req.QrPayload();
            var qr = Qr.Modules(payload);
            var L = OverlayLayout.Compute(w, h, qr.Length, name, meta, payload);

            DrawCentered(g, "LOCATE", L.LabelSize, FontStyle.Bold, Accent, L.LabelY);
            DrawCentered(g, name, L.NameSize, FontStyle.Bold, Color.White, L.NameY);
            DrawCentered(g, meta, L.MetaSize, FontStyle.Regular, Color.FromArgb(190, 200, 210), L.MetaY);

            // scannable QR (white pad + black modules on the dark stage), px-per-module from the layout
            int px = L.QrPx, modules = qr.Length;
            int qrSize = modules * px;
            var pad = 3 * px;
            var box = new Rectangle(w / 2 - qrSize / 2 - pad, (int)L.QrY, qrSize + 2 * pad, qrSize + 2 * pad);
            g.FillRectangle(Brushes.White, box);
            using var brush = new SolidBrush(Color.Black);
            for (int y = 0; y < modules; y++)
                for (int x = 0; x < modules; x++)
                    if (qr[y][x])
                        g.FillRectangle(brush, box.X + pad + x * px, box.Y + pad + y * px, px, px);

            DrawCentered(g, payload, L.PayloadSize, FontStyle.Regular, Color.FromArgb(150, 160, 170), L.PayloadY);

            // live auto-dismiss countdown
            DrawCentered(g, $"auto-dismiss in {_req.SecondsLeft(now)} s — click or press ESC to close",
                L.CountdownSize, FontStyle.Bold, Accent, L.CountdownY);
        }

        private void DrawCentered(System.Drawing.Graphics g, string text, float size, FontStyle style, Color color, float y)
        {
            var f = new Font("Segoe UI", size, style, GraphicsUnit.Pixel);
            var sz = g.MeasureString(text, f);
            while (sz.Width > ClientSize.Width * 0.94f && f.Size > 10)   // measured fit, not just estimated
            {
                var smaller = new Font(f.FontFamily, f.Size * 0.9f, f.Style, GraphicsUnit.Pixel);
                f.Dispose(); f = smaller; sz = g.MeasureString(text, f);
            }
            using (f)
                g.DrawString(text, f, new SolidBrush(color), (ClientSize.Width - sz.Width) / 2, y);
        }
    }
}
#endif
