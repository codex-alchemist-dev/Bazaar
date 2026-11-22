using System.Collections.Concurrent;
using System.Reflection;

namespace Bazaar.Server.WebAdmin.Assets;

/// <summary>
/// Serves the admin panel's static files from the assembly's embedded <c>wwwroot</c> resources (doc 51 section 4).
/// Paths are relative to <c>wwwroot</c> (for example <c>index.html</c> or <c>assets/panel.js</c>). Lookups use an exact
/// match against the manifest, so nothing reaches the file system and no path can escape the embedded set.
/// </summary>
public static class AdminAssets
{
    private const string _resourcePrefix = "Bazaar.Server.WebAdmin.wwwroot.";

    // Manifest suffix (for example "assets.panel.js") -> full manifest resource name. Built once.
    private static readonly Lazy<Dictionary<string, string>> _manifest = new(BuildManifest);

    // Only paths that exist in the manifest are cached, so unknown request paths cannot grow the cache.
    private static readonly ConcurrentDictionary<string, (byte[] Content, string ContentType)> _cache = new(StringComparer.Ordinal);

    /// <summary>Tries to read an embedded panel file. Returns false for unsafe or unknown paths.</summary>
    public static bool TryGet(string path, out byte[] content, out string contentType)
    {
        content = [];
        contentType = string.Empty;

        if (!IsSafePath(path, out var key))
        {
            return false;
        }

        if (_cache.TryGetValue(key, out var cached))
        {
            content = cached.Content;
            contentType = cached.ContentType;
            return true;
        }

        if (!_manifest.Value.TryGetValue(key, out var resourceName))
        {
            return false;
        }

        var bytes = ReadResource(resourceName);
        if (bytes is null)
        {
            return false;
        }

        contentType = ContentTypeFor(path);
        _cache[key] = (bytes, contentType);
        content = bytes;
        return true;
    }

    // Rejects traversal, backslashes, absolute paths and empty segments. On success, key is the manifest suffix.
    private static bool IsSafePath(string? path, out string key)
    {
        key = string.Empty;
        if (string.IsNullOrEmpty(path)
            || path[0] == '/'
            || path.Contains('\\', StringComparison.Ordinal)
            || path.Contains("..", StringComparison.Ordinal)
            || path.Contains("//", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var ch in path)
        {
            if (char.IsControl(ch))
            {
                return false;
            }
        }

        key = path.Replace('/', '.');
        return true;
    }

    private static Dictionary<string, string> BuildManifest()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in typeof(AdminAssets).Assembly.GetManifestResourceNames())
        {
            if (name.StartsWith(_resourcePrefix, StringComparison.Ordinal))
            {
                map[name[_resourcePrefix.Length..]] = name;
            }
        }

        return map;
    }

    private static byte[]? ReadResource(string resourceName)
    {
        using var stream = typeof(AdminAssets).Assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static string ContentTypeFor(string path)
    {
        var dot = path.LastIndexOf('.');
        var extension = dot < 0 ? string.Empty : path[dot..];
        return extension switch
        {
            ".html" => "text/html; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".js" => "text/javascript; charset=utf-8",
            _ => "application/octet-stream",
        };
    }
}
