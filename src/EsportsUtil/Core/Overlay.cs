using System.Text;
using QRCoder;

namespace EsportsUtil.Core;

/// <summary>Locate overlay — pure request model shared by the agent (which spawns the overlay surface),
/// the Windows WinForms renderer, and the dev console renderer. The server stamps the display_name into
/// the directive payload; the agent supplies pin/client_id from its identity. Expiry is honored at every
/// hop, so a late ack can never pop a stale overlay.</summary>
public sealed record LocateOverlayRequest(string DisplayName, string DevicePin, string ClientId, long ExpiresUnix, string? Model)
{
    public const string Flag = "--overlay";

    /// <summary>Build the child-process argv (after the executable) for the same-EXE overlay mode.</summary>
    public static List<string> BuildArgs(LocateOverlayRequest req) =>
    [
        Flag,
        "--display-name", req.DisplayName,
        "--pin", req.DevicePin,
        "--client-id", req.ClientId,
        "--expires", req.ExpiresUnix.ToString(),
        "--model", req.Model ?? "",
    ];

    /// <summary>Parse the overlay argv slice back into a request. False on malformed input.</summary>
    public static bool TryParse(IReadOnlyList<string> a, out LocateOverlayRequest req)
    {
        req = null!;
        string? name = null, pin = null, cid = null;
        long expires = 0;
        string? model = null;
        for (int i = 0; i < a.Count; i++)
            switch (a[i])
            {
                case "--display-name" when i + 1 < a.Count: name = a[++i]; break;
                case "--pin" when i + 1 < a.Count: pin = a[++i]; break;
                case "--client-id" when i + 1 < a.Count: cid = a[++i]; break;
                case "--expires" when i + 1 < a.Count && long.TryParse(a[++i], out var e): expires = e; break;
                case "--model" when i + 1 < a.Count: model = a[++i]; break;
            }
        if (name is null || pin is null || cid is null || expires <= 0) return false;
        req = new LocateOverlayRequest(name, pin, cid, expires, string.IsNullOrEmpty(model) ? null : model);
        return true;
    }

    /// <summary>Expiry guard: stale overlays are suppressed, never shown.</summary>
    public bool ShouldShow(long nowUnix) => ExpiresUnix > nowUnix;

    public int SecondsLeft(long nowUnix) => Math.Max(0, (int)(ExpiresUnix - nowUnix));

    /// <summary>Station metadata line under the giant name.</summary>
    public string MetaLine() =>
        $"PIN {DevicePin} · {(Model is null ? "model unknown" : Model)} · client {ClientId[..Math.Min(8, ClientId.Length)]}";

    /// <summary>Same line split into normal/strong runs — the mockup styles PIN and model emphasized
    /// against muted separators (pure so the WinForms renderer and tests share one source).</summary>
    public List<(string Text, bool Strong)> MetaRuns()
    {
        var runs = new List<(string, bool)> { ("PIN ", false), (DevicePin, true) };
        runs.Add((" · ", false));
        runs.Add((Model is null ? "model unknown" : Model, true));
        runs.Add(($" · client {ClientId[..Math.Min(8, ClientId.Length)]}", false));
        return runs;
    }

    /// <summary>QR payload identical to the printed label — one scan protocol covers both surfaces.</summary>
    public string QrPayload() => FileLabelPrinter.QrPayload(ClientId, DevicePin);
}

/// <summary>QR module matrix helper shared by the WinForms and console renderers (pure, testable anywhere).</summary>
public static class Qr
{
    public static bool[][] Modules(string payload)
    {
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(payload, QRCodeGenerator.ECCLevel.Q);
        var m = data.ModuleMatrix;                     // outer ring padding of false values included
        var rows = new bool[m.Count][];
        for (int y = 0; y < m.Count; y++)
        {
            rows[y] = new bool[m.Count];
            for (int x = 0; x < m.Count; x++) rows[y][x] = m[y][x];
        }
        return rows;
    }
}

/// <summary>Composition matching the approved mockup (mockups/locate_overlay.html): a horizontal card —
/// left-aligned kicker/name/meta block beside a white rounded QR card, everything auto-fit to the
/// viewport. Kicker/name/meta and the QR block never overlap; long names shrink instead of clipping.</summary>
public static class OverlayLayout
{
    public sealed record Layout(
        float Scale, float BorderInset, float BorderWidth,
        float KickerSize, float NameSize, float MetaSize,
        float IdLeft, float KickerY, float NameY, float MetaY, float IdEstWidth,
        float Gap, int QrModules, int QrCardPx, int QrPad, float QrX, float QrY,
        float QrLabelSize, float QrLabelY,
        float CountSize, float HintSize, float HintY);

    // Rough average advance width per char for Segoe UI (bold/regular) as a fraction of pixel size.
    private const float BoldFactor = 0.62f, RegularFactor = 0.52f;

    public static Layout Compute(int w, int h, int qrModules, string name, string meta)
    {
        float s = Math.Clamp(Math.Min(w / 1920f, h / 1080f), 0.55f, 2.2f);
        float borderInset = 14 * s, borderWidth = 4 * s;

        float kickerSize = Math.Clamp(13 * s, 11, 26);
        float metaSize = Math.Clamp(15 * s, 11, 30);
        float nameSize = Math.Clamp(w * 0.075f, 30, 76 * s);   // mockup: clamp(34px, 7.5vw, 76px) scaled
        float countSize = Math.Clamp(20 * s, 14, 34);
        float qrLabelSize = Math.Clamp(12 * s, 10, 22);
        float hintSize = Math.Clamp(12 * s, 10, 24);
        float hintY = h - hintSize - 30 * s;
        float gap = 44 * s;

        // White QR card (mockup 118px @1x + 10px inner padding); keep at least 3px per QR module.
        int qrPad = Math.Max(6, (int)(10 * s));
        int qrCard = (int)Math.Min(h * 0.34f, w * 0.42f);
        qrCard = Math.Max(qrCard, qrModules * 3 + 2 * qrPad);

        float avail = w - 2 * (borderInset + 24 * s);
        if (qrCard > avail * 0.42f) qrCard = (int)(avail * 0.42f);

        // Id block width estimate; shrink name/meta to keep the horizontal card fitting.
        float nameW = BoldFactor * nameSize * name.Length;
        float metaW = RegularFactor * metaSize * meta.Length;
        float idEst = Math.Max(nameW, metaW);
        float budget = avail - gap - qrCard;
        if (idEst > budget && budget > 0)
        {
            float k = budget / idEst;
            nameSize = Math.Max(24, nameSize * k);
            metaSize = Math.Max(11, metaSize * k);
            idEst = Math.Max(BoldFactor * nameSize * name.Length, RegularFactor * metaSize * meta.Length);
        }

        // Vertically center the id block; QR card centered with its label hanging below.
        float blockH = kickerSize * 1.5f + nameSize * 1.25f + metaSize * 1.4f;
        float top = (h - blockH) / 2;
        float kickerY = top;
        float nameY = top + kickerSize * 1.5f;
        float metaY = nameY + nameSize * 1.25f;

        float left = Math.Max(borderInset + 24 * s, (w - (idEst + gap + qrCard)) / 2);
        float qrX = left + idEst + gap;
        float qrY = (h - qrCard) / 2 - qrLabelSize * 0.9f;

        return new Layout(s, borderInset, borderWidth,
            kickerSize, nameSize, metaSize, left, kickerY, nameY, metaY, idEst,
            gap, qrModules, qrCard, qrPad, qrX, qrY,
            qrLabelSize, qrY + qrCard + 8 * s,
            countSize, hintSize, hintY);
    }
}

/// <summary>Dev/Linux console fallback: renders the same content (giant name, meta, scannable half-block QR,
/// countdown) in the terminal so the full pipeline is exercisable without a Windows desktop.</summary>
public static class ConsoleOverlay
{
    public static string RenderCard(LocateOverlayRequest req, long nowUnix)
    {
        const int inner = 52;
        var qr = Qr.Modules(req.QrPayload());
        string Row(string text) => $"┃{text,-inner}┃";
        var sb = new StringBuilder();
        sb.Append("┏").Append(new string('━', inner)).AppendLine("┓");
        sb.AppendLine(Row($" LOCATE — {req.DisplayName}"));
        sb.AppendLine(Row(" " + req.MetaLine()));
        sb.AppendLine(Row($" auto-dismiss in {req.SecondsLeft(nowUnix)}s"));
        sb.AppendLine("┣" + new string('─', inner) + "┫");
        // QRCoder's matrix already carries a 4-module white quiet zone — kept as-is, that margin makes
        // both the console and WinForms renders scannable. 2 QR rows per text line via half blocks (▀▄█).
        for (int y = 0; y + 1 < qr.Length; y += 2)
        {
            sb.Append("┃  ");
            for (int x = 0; x < qr[0].Length; x++)
            {
                bool top = qr[y][x], bot = qr[y + 1][x];
                sb.Append(top && bot ? '█' : top ? '▀' : bot ? '▄' : ' ');
            }
            sb.AppendLine();
        }
        sb.AppendLine("┗" + new string('─', inner) + "┛");
        sb.AppendLine($"scan QR payload: {req.QrPayload()}");
        return sb.ToString();
    }

    public static async Task RunAsync(LocateOverlayRequest req, CancellationToken ct)
    {
        Console.WriteLine("press ENTER to dismiss");
        var dismiss = Task.Run(() => { try { Console.ReadLine(); } catch (IOException) { } }, ct);
        while (!ct.IsCancellationRequested && req.ShouldShow(DateTimeOffset.UtcNow.ToUnixTimeSeconds()))
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Console.Clear();
            Console.WriteLine(RenderCard(req, now));
            // Enter pressed → close early (like click/ESC on Windows)
            if (await Task.WhenAny(dismiss, Task.Delay(1000, CancellationToken.None)) == dismiss) break;
        }
    }
}
