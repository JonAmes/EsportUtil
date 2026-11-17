namespace EsportsUtil.Core;

/// <summary>Magic-byte format gate for uploaded images. File names and client-declared MIME
/// types are never trusted — an .exe renamed to .png or a script-carrying SVG must not pass.
/// SVG is deliberately unsupported: XML can embed script, and we render logos inline.</summary>
public static class ImageFormats
{
    public const long MaxLogoBytes = 4L * 1024 * 1024;   // venue logos never need more
    public const long MaxWallpaperBytes = 8L * 1024 * 1024;   // wallpapers are legitimately bigger than logos

    public readonly record struct Known(string Ext, string Mime);

    /// <summary>Classifies a body by its header bytes; null = not a supported image.</summary>
    public static Known? Sniff(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47)
            return new Known("png", "image/png");
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF)
            return new Known("jpg", "image/jpeg");
        if (b.Length >= 2 && b[0] == (byte)'B' && b[1] == (byte)'M')
            return new Known("bmp", "image/bmp");
        if (b.Length >= 4 && b[0] == (byte)'G' && b[1] == (byte)'I' && b[2] == (byte)'F' && b[3] == (byte)'8')
            return new Known("gif", "image/gif");
        if (b.Length >= 12 && b[0] == (byte)'R' && b[1] == (byte)'I' && b[2] == (byte)'F' && b[3] == (byte)'F'
            && b[8] == (byte)'W' && b[9] == (byte)'E' && b[10] == (byte)'B' && b[11] == (byte)'P')
            return new Known("webp", "image/webp");
        return null;
    }
}
