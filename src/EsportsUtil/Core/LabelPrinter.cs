using System.Text;

namespace EsportsUtil.Core;

/// <summary>Label printing decoupled from registration. The station label is a physical
/// sticker (2in x 1in): station name + venue location + brand logo slot. Deliberately no
/// PIN and no client id — the label is for humans walking the venue floor, not machine auth.
/// Dev fallback writes a print-ready HTML artifact to data/labels/; the server serves it at
/// /labels/ so the dashboard opens it in a tab the operator prints.</summary>
public interface ILabelPrinter
{
    /// <summary>Returns the produced artifact path (print-ready .html label card in fallback).</summary>
    string Print(string displayName, string? location);
}

public sealed class FileLabelPrinter(string dir) : ILabelPrinter
{
    public string Print(string displayName, string? location)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"label-{Slug(displayName)}.html");
        File.WriteAllText(path, RenderHtml(Esc(displayName), Esc(location ?? "")));
        return path;
    }

    public static string Slug(string name)
    {
        var sb = new StringBuilder();
        foreach (var ch in name.ToLowerInvariant())
            sb.Append(char.IsAsciiLetterOrDigit(ch) ? ch : '-');
        var s = sb.ToString();
        while (s.Contains("--")) s = s.Replace("--", "-");
        s = s.Trim('-');
        return s.Length == 0 ? "station" : s;
    }

    public static string Esc(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    /// <summary>Single-file label matching mockups/label.html: white 2x1in sticker, logo
    /// placeholder left, name + location right. Logo slot stays a dashed box until the brand asset lands.</summary>
    public static string RenderHtml(string nameEsc, string locEsc)
    {
        var locHtml = locEsc.Length == 0 ? "" : $"<div class=\"loc\">{locEsc}</div>\n          ";
        return """
            <!DOCTYPE html><html lang="en"><head><meta charset="UTF-8"><title>Station Label</title>
            <style>
              *{box-sizing:border-box;margin:0;padding:0}
              @page{size:2in 1in;margin:0}
              html,body{background:#f3f3f5}
              body{font-family:-apple-system,'Segoe UI',Roboto,sans-serif;display:flex;flex-direction:column;
                   align-items:center;gap:14px;padding:24px}
              .label{width:508px;height:254px;background:#fff;color:#111;border-radius:14px;
                     box-shadow:0 6px 24px rgba(0,0,0,.15);display:flex;align-items:center;gap:40px;
                     padding:20px 24px;position:relative;overflow:hidden}
              .label::after{content:"";position:absolute;inset:0;border-radius:14px;
                            border:1px solid rgba(0,0,0,.18);pointer-events:none}
              .logo{width:186px;height:186px;flex:none;border-radius:12px;border:2px dashed #c9c9d2;
                    display:flex;align-items:center;justify-content:center;flex-direction:column;gap:4px;
                    color:#b6b6c2;font-size:15px;font-weight:700;letter-spacing:.22em}
              .logo small{font-size:9px;letter-spacing:.1em;font-weight:400}
              .edge{position:absolute;left:234px;top:14px;bottom:14px;width:1px;background:rgba(0,0,0,.08)}
              .id{display:flex;flex-direction:column;gap:8px;min-width:0;flex:1}
              .kicker{font-size:10px;font-weight:700;letter-spacing:.22em;color:#5b67ff;text-transform:uppercase}
              .name{font-size:36px;font-weight:700;letter-spacing:-.5px;line-height:1.05;
                    white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
              .loc{font-size:17px;color:#4b4b55;line-height:1.25}
              .bar{margin-top:10px;width:46px;height:4px;border-radius:999px;background:#5b67ff}
              .print{position:fixed;bottom:16px;right:16px;padding:9px 16px;border:0;border-radius:9px;
                     background:#111;color:#fff;font-size:13px;cursor:pointer}
              @media print{body{padding:0;background:#fff;gap:0}.print{display:none}
                           .label{box-shadow:none;border-radius:10px}}
            </style></head><body>
              <div class="label">
                <div class="logo">LOGO<small>brand slot &mdash; fill later</small></div>
                <div class="edge"></div>
                <div class="id">
                  <div class="kicker">E-Sports &middot; Station</div>
                  <div class="name">__NAME__</div>
                  __LOC__<div class="bar"></div>
                </div>
              </div>
              <button class="print" onclick="window.print()">Print label</button>
            </body></html>
            """.Replace("__NAME__", nameEsc).Replace("__LOC__", locHtml);
    }

    // Locate overlay QR protocol (labels no longer carry a QR, but the overlay still does).
    public static string QrPayload(string clientId, string devicePin) => $"ESU:{clientId}:{devicePin}";
}
