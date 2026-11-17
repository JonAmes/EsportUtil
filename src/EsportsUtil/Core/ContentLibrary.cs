using System.Security.Cryptography;

namespace EsportsUtil.Core;

/// <summary>Server side: Content Library file store + manifest (schemas/content_manifest.schema.json).
/// content_id is the only cross-wire handle — never a URL or a path.</summary>
public sealed class ContentLibrary
{
    private readonly string _dataDir;
    private ContentManifest? _cache;
    private DateTime _cacheStamp = DateTime.MinValue;

    public ContentLibrary(string dataDir) { _dataDir = dataDir; }

    private string ManifestPath => Path.Combine(_dataDir, "ContentManifest.json");
    public string FileDir => Path.Combine(_dataDir, "content");

    public ContentManifest Load()
    {
        var stamp = File.Exists(ManifestPath) ? File.GetLastWriteTimeUtc(ManifestPath) : DateTime.MinValue;
        if (_cache is not null && stamp == _cacheStamp) return _cache;
        _cache = File.Exists(ManifestPath)
            ? Wire.FromJson<ContentManifest>(File.ReadAllText(ManifestPath)) ?? new ContentManifest(0, DateTimeOffset.UtcNow, new())
            : new ContentManifest(0, DateTimeOffset.UtcNow, new());
        _cacheStamp = stamp;
        return _cache;
    }

    public ContentItem? Find(string contentId) => Load().Items.FirstOrDefault(i => i.ContentId == contentId);

    public string? FilePathFor(string contentId)
    {
        var item = Find(contentId);
        if (item is null) return null;
        var path = Path.Combine(FileDir, item.Filename);
        return File.Exists(path) ? path : null;
    }
}

/// <summary>Agent side: sha256-keyed idempotent cache under ProgramData\EsportsUtil\content
/// (dev/Linux: dataDir/content-cache). A cached hash match means no re-download.</summary>
public sealed class ContentCache(HttpClient http, string cacheDir)
{
    public async Task<(ContentItem Item, bool Downloaded)> EnsureAsync(string serverUrl, ContentItem item, CancellationToken ct = default)
    {
        Directory.CreateDirectory(cacheDir);
        var path = Path.Combine(cacheDir, item.ContentId, item.Filename);
        if (File.Exists(path) && await Sha256File(path, ct) == item.Sha256)
            return (item, false);

        using var resp = await http.GetAsync($"{serverUrl.TrimEnd('/')}/content/{item.ContentId}", ct);
        resp.EnsureSuccessStatusCode();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        await using (var fs = File.Create(tmp))
            await resp.Content.CopyToAsync(fs, ct);
        if (await Sha256File(tmp, ct) != item.Sha256)
        {
            File.Delete(tmp);
            throw new InvalidDataException($"sha256 mismatch for content '{item.ContentId}'");
        }
        File.Move(tmp, path, overwrite: true);
        return (item, true);
    }

    public static async Task<string> Sha256File(string path, CancellationToken ct = default)
    {
        await using var fs = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(fs, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
