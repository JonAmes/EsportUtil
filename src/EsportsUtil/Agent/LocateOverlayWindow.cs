#if WINDOWS
using System.Runtime.Versioning;
using EsportsUtil.Core;

namespace EsportsUtil.Agent;

/// <summary>Phase 4 locate overlay (PLAN.md): full-screen topmost window across the virtual desktop —
/// giant display_name, station metadata line, QR encoding the DevicePin (same payload as the printed
/// label), pulsing accent border, live auto-dismiss countdown. Closes on expiry, click, or ESC.
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
            Shown += (_, _) => { Activate(); Focus(); };
            _timer.Tick += (_, _) =>
            {
                _pulse = !_pulse;
                if (!_req.ShouldShow(DateTimeOffset.UtcNow.ToUnixTimeSeconds())) { Close(); return; }
                Invalidate();
            };
            _timer.Start();
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

            // giant name, auto-sized to the screen
            var name = _req.DisplayName.ToUpperInvariant();
            var nameSize = Math.Clamp(w / Math.Max(8, name.Length / 2), 48, 220);
            DrawCentered(g, name, nameSize, FontStyle.Bold, Color.White, h * 0.16f);
            DrawCentered(g, "LOCATE", nameSize * 0.28f, FontStyle.Bold, Accent, h * 0.10f);
            DrawCentered(g, _req.MetaLine(), Math.Max(18, nameSize / 4), FontStyle.Regular, Color.FromArgb(190, 200, 210), h * 0.30f);

            // scannable QR (white pad + black modules on the dark stage)
            var qr = Qr.Modules(_req.QrPayload());
            int modules = qr.Length;
            int px = Math.Clamp(Math.Min(w, h) / (modules + 8), 4, 14);
            int qrSize = modules * px;
            var pad = 3 * px;
            var box = new Rectangle(w / 2 - qrSize / 2 - pad, (int)(h * 0.38f) - pad, qrSize + 2 * pad, qrSize + 2 * pad);
            g.FillRectangle(Brushes.White, box);
            using var brush = new SolidBrush(Color.Black);
            for (int y = 0; y < modules; y++)
                for (int x = 0; x < modules; x++)
                    if (qr[y][x])
                        g.FillRectangle(brush, box.X + pad + x * px, box.Y + pad + y * px, px, px);

            DrawCentered(g, _req.QrPayload(), Math.Max(14, nameSize / 6), FontStyle.Regular, Color.FromArgb(150, 160, 170), box.Bottom + h * 0.03f);

            // live auto-dismiss countdown
            DrawCentered(g, $"auto-dismiss in {_req.SecondsLeft(now)} s — click or press ESC to close",
                Math.Max(18, nameSize / 4), FontStyle.Bold, Accent, h * 0.90f);
        }

        private void DrawCentered(System.Drawing.Graphics g, string text, float size, FontStyle style, Color color, float y)
        {
            using var f = new Font("Segoe UI", size, style, GraphicsUnit.Pixel);
            var sz = g.MeasureString(text, f);
            g.DrawString(text, f, new SolidBrush(color), (ClientSize.Width - sz.Width) / 2, y);
        }
    }
}
#endif
