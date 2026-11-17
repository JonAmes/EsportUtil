#if WINDOWS
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace EsportsUtil.Core;

/// <summary>First-run role picker (Windows): "This PC is a SERVER / This PC is an AGENT",
/// then a server-address box for agents with a live reachability probe. Dark styling matches
/// the locate overlay. Returns null if the operator cancels — nothing is written then.</summary>
[SupportedOSPlatform("windows10.0.19041")]
public static class FirstRunDialog
{
    public static (string Role, string? ServerUrl)? Run()
    {
        (string, string?)? picked = null;
        var th = new Thread(() =>
        {
            Application.EnableVisualStyles();
            using var f = new SetupForm();
            f.ShowDialog();
            picked = f.Picked;
        }) { IsBackground = true };
        th.SetApartmentState(ApartmentState.STA);
        th.Start();
        th.Join();
        return picked;
    }

    private sealed class SetupForm : Form
    {
        public (string, string?)? Picked { get; private set; }

        private static readonly Color BaseColor = Color.FromArgb(13, 15, 20);      // mockup #0d0f14
        private static readonly Color PanelColor = Color.FromArgb(22, 22, 26);     // mockup card #16161a
        private static readonly Color Accent = Color.FromArgb(109, 141, 255);      // mockup #6d8dff

        private readonly TextBox _url = new();
        private readonly Label _status = new();
        private readonly Panel _step1 = new();
        private readonly Panel _step2 = new();

        public SetupForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(560, 400);
            BackColor = BaseColor;
            KeyPreview = true;
            KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
            MouseDown += (_, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    ReleaseCapture();
                    SendMessage(Handle, 0x0112 /*WM_SYSCOMMAND*/, 0xF012 /*SC_DRAGMOVE*/, 0);
                }
            };

            var title = MakeLabel("E-SPORTS DASHBOARD — FIRST RUN", 16, FontStyle.Bold, 46);
            var sub = MakeLabel("No config found. What should this PC run?", 11, FontStyle.Regular, 86);

            _step1.SetBounds(0, 140, ClientSize.Width, 220);
            _step2.SetBounds(0, 140, ClientSize.Width, 240);
            _step2.Visible = false;

            var btnServer = MakeButton("SERVER", "venue control machine — dashboard + fleet jobs");
            var btnAgent = MakeButton("AGENT", "game PC — reports to the server");
            btnServer.SetBounds(50, 0, 460, 70);
            btnAgent.SetBounds(50, 90, 460, 70);
            btnServer.Click += (_, _) => SaveServer();
            btnAgent.Click += (_, _) => ShowStep2();
            var quit = MakeLabel("quit", 10, FontStyle.Regular, 172, Color.FromArgb(150, 155, 165));
            quit.Cursor = Cursors.Hand;
            quit.Click += (_, _) => Close();
            _step1.Controls.AddRange(new Control[] { btnServer, btnAgent, quit });

            var ask = MakeLabel("Server address — IP or full URL:", 11, FontStyle.Regular, 16, Accent);
            ask.TextAlign = ContentAlignment.MiddleCenter;
            _url.SetBounds(50, 60, 460, 34);
            _url.Font = new Font("Segoe UI", 13f);
            _url.BackColor = PanelColor;
            _url.ForeColor = Color.White;
            _url.BorderStyle = BorderStyle.FixedSingle;
            _url.Text = "http://";
            _url.Select(_url.Text.Length, 0);
            _url.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) Save(); };

            var save = MakeButton("SAVE", "writes esportsutil.config.json next to the exe");
            save.SetBounds(50, 110, 220, 54);
            save.Click += (_, _) => Save();
            var back = MakeButton("BACK", "choose the role again");
            back.SetBounds(290, 110, 220, 54);
            back.Click += (_, _) => { _step2.Visible = false; _step1.Visible = true; };

            _status.SetBounds(50, 180, 460, 40);
            _status.AutoSize = false;
            _status.Font = new Font("Segoe UI", 10f);
            _status.ForeColor = Color.FromArgb(150, 155, 165);
            _step2.Controls.AddRange(new Control[] { ask, _url, save, back, _status });

            Controls.AddRange(new Control[] { title, sub, _step1, _step2 });
            _step1.BringToFront();
        }

        private void ShowStep2()
        {
            _step1.Visible = false;
            _step2.Visible = true;
            _url.Focus();
        }

        private async void Save()
        {
            var url = FirstRunSetup.NormalizeServerUrl(_url.Text);
            if (url is null)
            {
                _status.ForeColor = Color.FromArgb(255, 110, 110);
                _status.Text = "that doesn't look like an address — try 10.140.0.212 or http://host:4300";
                return;
            }
            _status.ForeColor = Color.FromArgb(150, 155, 165);
            _status.Text = "checking reachability…";
            var ok = await ProbeAsync(url);
            // save regardless: an unreachable server now is not a reason to lose the setup —
            // the agent retries its heartbeat forever once the box comes up
            _status.ForeColor = ok ? Color.FromArgb(90, 230, 140) : Color.FromArgb(255, 200, 90);
            _status.Text = ok ? "server reachable — config saved ✓" : "server unreachable right now — config saved; the agent keeps retrying";
            Picked = ("agent", url);
            await Task.Delay(1200);
            Close();
        }

        private void SaveServer()
        {
            Picked = ("server", null);   // server config needs no address — it binds its own port
            Close();
        }

        private Button MakeButton(string text, string note)
        {
            var b = new Button
            {
                Text = $"{text}\n{note}",
                FlatStyle = FlatStyle.Flat,
                BackColor = PanelColor,
                ForeColor = Color.FromArgb(230, 230, 234),
                Font = new Font("Segoe UI", 13f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleCenter,
            };
            b.FlatAppearance.BorderColor = Accent;
            b.FlatAppearance.BorderSize = 1;
            b.MouseEnter += (_, _) => b.BackColor = Color.FromArgb(35, 35, 41);
            b.MouseLeave += (_, _) => b.BackColor = PanelColor;
            return b;
        }

        private static async Task<bool> ProbeAsync(string url)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                using var resp = await http.GetAsync(url + "/api/state");
                return resp.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        private Label MakeLabel(string text, float pt, FontStyle style, int y, Color? color = null)
        {
            return new Label
            {
                Text = text,
                ForeColor = color ?? Color.White,
                Font = new Font("Segoe UI", pt, style),
                Location = new Point(0, y),
                Size = new Size(ClientSize.Width, (int)(pt * 2.4f)),
                TextAlign = ContentAlignment.MiddleCenter,
            };
        }

        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int w, int l);
    }
}
#endif
