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

    /// <summary>Stable ids this codebase mints itself — content is referenced by id everywhere, never by URL or path.</summary>
    public static class ContentIds
    {
        public const string VenueWallpaper = "venue-wallpaper";   // settings-page upload; presence = venue-wide desired state
    }

    /// <summary>Operator upload (settings page): atomically write the bytes under FileDir and upsert the
    /// manifest item. Re-uploading the same id replaces the previous file; version bumps on every mutation
    /// so agents' sha256-keyed caches see the change.</summary>
    public ContentItem Upsert(string contentId, string filename, string category, byte[] bytes)
    {
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        Directory.CreateDirectory(FileDir);
        var old = Find(contentId);
        var path = Path.Combine(FileDir, filename);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
        if (old is not null && old.Filename != filename)
        {
            var stale = Path.Combine(FileDir, old.Filename);
            if (File.Exists(stale)) File.Delete(stale);   // format swap leaves exactly one file
        }
        var items = Load().Items.ToList();
        items.RemoveAll(i => i.ContentId == contentId);
        var item = new ContentItem(contentId, filename, sha, category, bytes.Length);
        items.Add(item);
        SaveManifest(new ContentManifest(Load().Version + 1, DateTimeOffset.UtcNow, items));
        return item;
    }

    public bool Remove(string contentId)
    {
        var old = Find(contentId);
        if (old is null) return false;
        var p = Path.Combine(FileDir, old.Filename);
        if (File.Exists(p)) File.Delete(p);
        SaveManifest(new ContentManifest(Load().Version + 1, DateTimeOffset.UtcNow,
            Load().Items.Where(i => i.ContentId != contentId).ToList()));
        return true;
    }

    private void SaveManifest(ContentManifest m)
    {
        File.WriteAllText(ManifestPath, Wire.ToJson(m));
        _cache = m;
        _cacheStamp = File.GetLastWriteTimeUtc(ManifestPath);
    }

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
