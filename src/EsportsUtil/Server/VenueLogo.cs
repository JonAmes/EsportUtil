namespace EsportsUtil.Server;

public sealed record LogoInfo(string Format, long Bytes, long UpdatedUnix);

/// <summary>Venue logo store: exactly one logo at &lt;dataDir&gt;/logo.&lt;ext&gt;, discovered by
/// glob so a format swap can never leave orphaned assets behind. Saves are atomic
/// (temp write + move) so a crash mid-upload can't corrupt the only copy.</summary>
public sealed class VenueLogo(string dataDir)
{
    public string? FindPath() =>
        Directory.Exists(dataDir)
            ? Directory.GetFiles(dataDir, "logo.*").OrderBy(f => f, StringComparer.Ordinal).FirstOrDefault()
            : null;

    public LogoInfo? Info()
    {
        var p = FindPath();
        if (p is null) return null;
        var fi = new FileInfo(p);
        return new LogoInfo(
            Path.GetExtension(p).TrimStart('.').ToLowerInvariant(),
            fi.Length,
            new DateTimeOffset(fi.LastWriteTimeUtc, TimeSpan.Zero).ToUnixTimeSeconds());
    }

    /// <summary>bytes arrive pre-sniffed by the caller; writes logo.ext and deletes any stale logo.*.</summary>
    public LogoInfo Save(byte[] bytes, string ext)
    {
        Directory.CreateDirectory(dataDir);
        foreach (var old in Directory.GetFiles(dataDir, "logo.*"))
            try { File.Delete(old); } catch (IOException) { }   // best-effort; the new save is what matters
        var path = Path.Combine(dataDir, "logo." + ext);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
        return Info()!;
    }

    public bool Delete()
    {
        var p = FindPath();
        if (p is null) return false;
        File.Delete(p);
        return true;
    }
}
