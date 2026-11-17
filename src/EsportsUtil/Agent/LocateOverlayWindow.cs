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
}

/// <summary>Sticky downed overlay — identical renderer to the locate overlay (see OverlayForm),
/// driven by DownedOverlayRequest: no expiry, amber (#d97706) accent kept from the dashboard's
/// downed token, clicks ignored, staff bypass via B/ESC key or the drawn BYPASS chip. The agent
/// supervises its lifetime: killed when the operator returns the machine to active.</summary>
[SupportedOSPlatform("windows10.0.19041")]
public static class DownedOverlayWindow
{
    public static void Run(DownedOverlayRequest req)
    {
        var t = new Thread(() => System.Windows.Forms.Application.Run(new OverlayForm(req)));
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
        t.Join();
    }
}

internal sealed class OverlayForm : System.Windows.Forms.Form
{
    private static readonly Color Base = Color.FromArgb(13, 15, 20);      // mockup #0d0f14 stage
    private static readonly Color Fg = Color.FromArgb(230, 230, 234);     // mockup --fg
    private static readonly Color Muted = Color.FromArgb(155, 155, 166);  // mockup --mut
    private readonly IOverlayView _view;
    private readonly Color _accent;                                       // per-state palette token
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 575 };  // ~1.15s pulse cycle
    private bool _pulse;
    private RectangleF _bypassRect;                                       // hit area for the sticky downed bypass

    public OverlayForm(IOverlayView view)
    {
        _view = view;
        _accent = ColorTranslator.FromHtml(view.AccentHex);
        Text = $"{(view.HasCountdown ? "LOCATE" : "OUT OF SERVICE")} — {view.DisplayName}";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = SystemInformation.VirtualScreen;    // span every monitor on the box
        TopMost = true;
        ShowInTaskbar = false;
        BackColor = Base;
        ForeColor = Fg;
        DoubleBuffered = true;
        KeyPreview = true;
        // locate: ESC dismisses. downed: B (or ESC) is the staff bypass — never a timed close.
        KeyDown += (_, e) => { if (e.KeyCode == System.Windows.Forms.Keys.Escape || (!view.HasCountdown && e.KeyCode == System.Windows.Forms.Keys.B)) Close(); };
        // locate: any click dismisses. downed: sticky — only the BYPASS chip does.
        MouseDown += (_, e) => { if (view.HasCountdown || _bypassRect.Contains(e.Location)) Close(); };
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
            if (!_view.ShouldShow(DateTimeOffset.UtcNow.ToUnixTimeSeconds())) { Close(); return; }
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
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var w = ClientSize.Width;
            var h = ClientSize.Height;

            // Stage: near-black with the mockup's corner glows, sized to the window
            // (mockup: radial 120%x90% at 20% 10%, radial 80%x70% at 85% 90%).
            g.Clear(Base);
            DrawGlow(g, new Rectangle((int)(-0.4f * w), (int)(-0.35f * h), (int)(1.2f * w), (int)(0.9f * h)), Color.FromArgb(60, 22, 35, 61));
            DrawGlow(g, new Rectangle((int)(0.45f * w), (int)(0.55f * h), (int)(0.8f * w), (int)(0.7f * h)), Color.FromArgb(45, 27, 20, 48));

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var name = _view.DisplayName.ToUpperInvariant();
            var qr = Qr.Modules(_view.QrPayload());
            var L = OverlayLayout.Compute(w, h, qr.Length, name, _view.MetaLine());

            // Pulsing accent border (mockup: 4px, 14px inset, rounded, glow at peak).
            var border = RoundRect(L.BorderInset, L.BorderInset, w - 2 * L.BorderInset, h - 2 * L.BorderInset, 14 * L.Scale);
            using (var glowPen = new Pen(Color.FromArgb(_pulse ? 60 : 20, _accent), L.BorderWidth * 3f))
                g.DrawPath(glowPen, border);
            using (var pen = new Pen(Color.FromArgb(_pulse ? 255 : 110, _accent), L.BorderWidth))
                g.DrawPath(pen, border);
            border.Dispose();

            // Id block — kicker (letter-spaced accent), giant name, strong-run meta.
            var idBudget = Math.Max(100, L.QrX - L.IdLeft - L.Gap * 0.5f);
            DrawText(g, Spaced(_view.Kicker), L.KickerSize, FontStyle.Bold, _accent, L.IdLeft, L.KickerY, idBudget);
            DrawText(g, name, L.NameSize, FontStyle.Bold, Fg, L.IdLeft, L.NameY, idBudget);
            DrawRuns(g, _view.MetaRuns(), L.MetaSize, Muted, Fg, L.IdLeft, L.MetaY, idBudget);

            // White rounded QR card with black modules + hanging caption (identical payload to the label).
            int card = L.QrCardPx;
            using (var cardPath = RoundRect(L.QrX, L.QrY, card, card, 10 * L.Scale))
                g.FillPath(Brushes.White, cardPath);
            int px = Math.Max(1, (card - 2 * L.QrPad) / L.QrModules);
            int codeSize = px * L.QrModules;
            int ox = (int)L.QrX + (card - codeSize) / 2, oy = (int)L.QrY + (card - codeSize) / 2;
            using var dark = new SolidBrush(Color.FromArgb(17, 17, 17));
            for (int y = 0; y < L.QrModules; y++)
                for (int x = 0; x < L.QrModules; x++)
                    if (qr[y][x])
                        g.FillRectangle(dark, ox + x * px, oy + y * px, px, px);
            DrawCentered(g, "scan = device identity (same payload as label)", L.QrLabelSize, FontStyle.Regular, Muted, L.QrLabelY, (int)L.QrX + card / 2);

            // Countdown top-right (mockup) for locate; the sticky downed overlay has no expiry and
            // shows a pulsing BYPASS chip bottom-right instead. Dismiss hint stays bottom-center.
            var cf = new Font("Segoe UI", L.CountSize, FontStyle.Bold, GraphicsUnit.Pixel);
            if (_view.HasCountdown)
            {
                var sec = $"{_view.SecondsLeft(now)}s";
                var secSz = g.MeasureString(sec, cf);
                g.DrawString(sec, cf, new SolidBrush(_accent), w - 36 * L.Scale - secSz.Width, 28 * L.Scale);
                cf.Dispose();
                DrawRight(g, "auto-dismiss", Math.Max(10, L.CountSize * 0.55f), FontStyle.Regular, Muted, w - 36 * L.Scale, 28 * L.Scale + secSz.Height);
            }
            else
            {
                const string chip = "BYPASS  (B)";
                var chipSz = g.MeasureString(chip, cf);
                float cw = chipSz.Width + 28 * L.Scale, chh = chipSz.Height + 12 * L.Scale;
                _bypassRect = new RectangleF(w - 36 * L.Scale - cw, L.HintY - chh - 18 * L.Scale, cw, chh);
                using var chipPath = RoundRect(_bypassRect.X, _bypassRect.Y, cw, chh, 10 * L.Scale);
                using var chipFill = new SolidBrush(Color.FromArgb(_pulse ? 40 : 18, _accent));
                g.FillPath(chipFill, chipPath);
                using var chipPen = new Pen(Color.FromArgb(_pulse ? 255 : 165, _accent), Math.Max(1.5f, 2f * L.Scale));
                g.DrawPath(chipPen, chipPath);
                using var chipAcc = new SolidBrush(_accent);
                g.DrawString(chip, cf, chipAcc, _bypassRect.X + 14 * L.Scale, _bypassRect.Y + 6 * L.Scale);
                cf.Dispose();
                DrawRight(g, "no expiry — stays until bypass or operator clears", Math.Max(10, L.CountSize * 0.55f), FontStyle.Regular, Muted, w - 36 * L.Scale, _bypassRect.Y - L.CountSize * 0.8f);
            }
            DrawCentered(g, _view.DismissHint, L.HintSize, FontStyle.Regular, Muted, L.HintY, w / 2);
        }

        private static string Spaced(string s)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var c in s) { if (sb.Length > 0) sb.Append(' '); sb.Append(c); }
            return sb.ToString();
        }

        private static System.Drawing.Drawing2D.GraphicsPath RoundRect(float x, float y, float w, float h, float r)
        {
            r = Math.Min(r, Math.Min(w, h) / 2);
            var p = new System.Drawing.Drawing2D.GraphicsPath();
            p.AddArc(x, y, r * 2, r * 2, 180, 90);
            p.AddArc(x + w - r * 2, y, r * 2, r * 2, 270, 90);
            p.AddArc(x + w - r * 2, y + h - r * 2, r * 2, r * 2, 0, 90);
            p.AddArc(x, y + h - r * 2, r * 2, r * 2, 90, 90);
            p.CloseFigure();
            return p;
        }

        private static void DrawGlow(System.Drawing.Graphics g, Rectangle r, Color center)
        {
            using var path = new System.Drawing.Drawing2D.GraphicsPath();
            path.AddEllipse(r);
            using var pgb = new System.Drawing.Drawing2D.PathGradientBrush(path)
            {
                CenterColor = center,
                SurroundColors = [Color.FromArgb(0, center.R, center.G, center.B)],
            };
            g.FillEllipse(pgb, r);
        }

        /// <summary>Left-aligned at (x, y); measured-fit shrinks the font (never the position) so long text can't clip.</summary>
        private void DrawText(System.Drawing.Graphics g, string text, float size, FontStyle style, Color color, float x, float y, float maxWidth)
        {
            var f = new Font("Segoe UI", size, style, GraphicsUnit.Pixel);
            var sz = g.MeasureString(text, f);
            while (sz.Width > maxWidth && f.Size > 10)
            {
                var smaller = new Font(f.FontFamily, f.Size * 0.9f, f.Style, GraphicsUnit.Pixel);
                f.Dispose(); f = smaller; sz = g.MeasureString(text, f);
            }
            using (f)
                g.DrawString(text, f, new SolidBrush(color), x, y);
        }

        /// <summary>Consecutive runs on one baseline; strong runs get SemiBold + full color (mockup meta line).</summary>
        private void DrawRuns(System.Drawing.Graphics g, List<(string Text, bool Strong)> runs, float size, Color muted, Color strong, float x, float y, float maxWidth)
        {
            float total;
            do
            {
                total = 0;
                foreach (var (t, st) in runs)
                    using (var f = new Font("Segoe UI", size, st ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel))
                        total += g.MeasureString(t, f).Width;
                if (total <= maxWidth || size <= 9) break;
                size *= 0.9f;
            } while (true);
            float cx = x;
            foreach (var (t, st) in runs)
            {
                var f = new Font("Segoe UI", size, st ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
                var sz = g.MeasureString(t, f);
                g.DrawString(t, f, new SolidBrush(st ? strong : muted), cx, y);
                cx += sz.Width;
                f.Dispose();
            }
        }

        private void DrawRight(System.Drawing.Graphics g, string text, float size, FontStyle style, Color color, float rightX, float y)
        {
            using var f = new Font("Segoe UI", size, style, GraphicsUnit.Pixel);
            var sz = g.MeasureString(text, f);
            g.DrawString(text, f, new SolidBrush(color), rightX - sz.Width, y);
        }

        private void DrawCentered(System.Drawing.Graphics g, string text, float size, FontStyle style, Color color, float y, int centerX)
        {
            using var f = new Font("Segoe UI", size, style, GraphicsUnit.Pixel);
            var sz = g.MeasureString(text, f);
            g.DrawString(text, f, new SolidBrush(color), centerX - sz.Width / 2, y);
        }
    }
#endif
