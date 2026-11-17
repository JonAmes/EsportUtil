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

/// <summary>Pure stacked-flow layout for the fullscreen overlay (px sizes + Y cursors), testable on any
/// OS. Everything auto-fits: name/meta shrink to stay inside the width budget, the QR scales to the
/// height budget, and lines never overlap — the previous fixed-position giant text clipped/overlapped.</summary>
public static class OverlayLayout
{
    public sealed record Layout(
        float LabelSize, float LabelY,
        float NameSize, float NameY,
        float MetaSize, float MetaY,
        int QrPx, float QrY, int QrModules,
        float PayloadSize, float PayloadY,
        float CountdownSize, float CountdownY);

    // Rough average advance width per char for Segoe UI (bold/regular) as a fraction of pixel size.
    private const float BoldFactor = 0.62f, RegularFactor = 0.52f;

    public static Layout Compute(int w, int h, int qrModules, string name, string meta, string payloadLine)
    {
        float labelSize = Math.Clamp(h * 0.032f, 14, 34);
        float countdownSize = Math.Clamp(h * 0.030f, 14, 28);
        float payloadSize = Math.Clamp(h * 0.022f, 12, 22);
        float metaSize = Math.Clamp(h * 0.030f, 14, 28);

        // Name: generous but capped, then shrink-to-fit the 80% width budget.
        float nameSize = Math.Clamp(h * 0.10f, 28, 150);
        float nameWidth = BoldFactor * nameSize * name.Length;
        if (nameWidth > w * 0.80f) nameSize = Math.Max(24, nameSize * w * 0.80f / nameWidth);

        if (RegularFactor * metaSize * meta.Length > w * 0.85f)
            metaSize = Math.Max(12, metaSize * w * 0.85f / (RegularFactor * meta.Length));
        if (RegularFactor * payloadSize * payloadLine.Length > w * 0.90f)
            payloadSize = Math.Max(11, payloadSize * w * 0.90f / (RegularFactor * payloadLine.Length));

        // QR: fit inside 36% of height and 45% of width (including its white pad ring of 3 modules).
        int qrPx = Math.Clamp((int)(Math.Min(h * 0.36f, w * 0.45f) / (qrModules + 6)), 3, 14);
        float qrOuter = qrPx * (qrModules + 6);

        // Stack top-to-bottom; countdown pinned near the bottom.
        float y = h * 0.08f;
        float labelY = y; y += labelSize * 1.3f;
        float nameY = y; y += nameSize * 1.3f + h * 0.01f;
        float metaY = y; y += metaSize * 1.3f + h * 0.025f;
        float qrY = y; y += qrOuter + h * 0.012f;
        float payloadY = y;

        float countdownY = h - countdownSize - h * 0.035f;
        // Safety: if anything still overruns, callers shrink nothing further — the budgets above prevent it.
        return new Layout(labelSize, labelY, nameSize, nameY, metaSize, metaY,
            qrPx, qrY, qrModules, payloadSize, payloadY, countdownSize, countdownY);
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
