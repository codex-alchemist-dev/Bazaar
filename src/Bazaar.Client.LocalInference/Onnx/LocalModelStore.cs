namespace Bazaar.Client.LocalInference.Onnx;

/// <summary>
/// Installed model files, laid out as <c>{root}/{model}/{version}/{file}</c>. Names come from a signed manifest,
/// so any name that is not a single path segment is refused.
/// </summary>
public sealed class LocalModelStore
{
    /// <summary>Creates a store rooted at <paramref name="root"/>.</summary>
    public LocalModelStore(string root) => Root = Path.GetFullPath(root);

    /// <summary>Absolute root directory of the store.</summary>
    public string Root { get; }

    /// <summary>Returns the path a manifest file would occupy in this store. It does not check that the file exists.</summary>
    public string ResolveFile(ModelManifest manifest, ManifestFile file)
    {
        RequireSegment(manifest.Model, nameof(manifest));
        RequireSegment(manifest.Version, nameof(manifest));
        RequireSegment(file.Name, nameof(file));
        return Path.Combine(Root, manifest.Model, manifest.Version, file.Name);
    }

    private static void RequireSegment(string segment, string paramName)
    {
        if (segment is "" or "." or ".." || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || segment.Contains(Path.DirectorySeparatorChar) || segment.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new ArgumentException($"'{segment}' is not a single safe path segment.", paramName);
        }
    }
}
