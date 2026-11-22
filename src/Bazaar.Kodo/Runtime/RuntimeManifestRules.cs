namespace Bazaar.Kodo.Runtime;

/// <summary>
/// Layout rules a manifest must meet before its signature is even considered. They keep the canonical form unique
/// (sorted, unique file list) and stop a signed manifest from naming a path outside the slot.
/// </summary>
internal static class RuntimeManifestRules
{
    private const int _maxPathLength = 1024;
    private const int _sha256HexLength = 64;

    /// <summary>True when the manifest is structurally valid.</summary>
    public static bool IsWellFormed(RuntimeManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.Version)
            || string.IsNullOrWhiteSpace(manifest.BuildId)
            || !RuntimeLayout.IsSlotId(manifest.SlotId)
            || manifest.Files is null)
        {
            return false;
        }

        // Paths must be strictly ascending (ordinal, so the order is unique) and unique when case is ignored,
        // because NTFS treats names that differ only by case as one file.
        string? previous = null;
        HashSet<string> foldedPaths = new(StringComparer.OrdinalIgnoreCase);
        foreach (RuntimeFileEntry? entry in manifest.Files)
        {
            if (entry is null
                || !IsSafeRelativePath(entry.Path)
                || !IsSha256Hex(entry.Sha256)
                || entry.Size < 0)
            {
                return false;
            }

            if (previous is not null && string.CompareOrdinal(previous, entry.Path) >= 0)
            {
                return false;
            }

            if (!foldedPaths.Add(entry.Path))
            {
                return false;
            }

            previous = entry.Path;
        }

        return true;
    }

    /// <summary>True for 64 lowercase hex characters.</summary>
    public static bool IsSha256Hex(string? value)
    {
        if (value is null || value.Length != _sha256HexLength)
        {
            return false;
        }

        foreach (char c in value)
        {
            if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>True for a relative path with '/' separators, no empty, "." or ".." segments, and no drive or control characters.</summary>
    public static bool IsSafeRelativePath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > _maxPathLength || path[0] == '/')
        {
            return false;
        }

        foreach (char c in path)
        {
            if (c < ' ' || c is '\\' or ':')
            {
                return false;
            }
        }

        foreach (string segment in path.Split('/'))
        {
            if (segment is "" or "." or "..")
            {
                return false;
            }
        }

        return true;
    }
}
