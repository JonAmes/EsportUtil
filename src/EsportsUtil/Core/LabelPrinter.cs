namespace EsportsUtil.Core;

/// <summary>Label printing decoupled from registration. Windows: named local print queue
/// (Microsoft Print to PDF in demo). Linux dev fallback: rendered label written to data/labels/.</summary>
public interface ILabelPrinter
{
    /// <summary>Returns the produced artifact path (PDF on Windows, .txt label card in fallback).</summary>
    string Print(string displayName, string devicePin, string clientId);
}

public sealed class FileLabelPrinter(string dir) : ILabelPrinter
{
    public string Print(string displayName, string devicePin, string clientId)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"label-{displayName.ToLowerInvariant().Replace(' ', '-')}-{devicePin}.txt");
        // QR payload kept identical to the locate overlay's QR so one scan protocol covers both.
        File.WriteAllText(path,
            $"┌──────────────────────────────┐\n" +
            $"│  {displayName,-28}│\n" +
            $"│  PIN {devicePin}                       │\n" +
            $"│  QR payload: {QrPayload(clientId, devicePin)}\n" +
            $"└──────────────────────────────┘\n");
        return path;
    }

    public static string QrPayload(string clientId, string devicePin) => $"ESU:{clientId}:{devicePin}";
}
