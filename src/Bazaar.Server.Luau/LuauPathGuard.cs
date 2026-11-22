using System.IO;
using Bazaar.Server.Luau.Abstractions;

namespace Bazaar.Server.Luau;

/// <summary>
/// Keeps Luau check inputs inside one project root. Each file is resolved against the root, normalised, and must stay
/// under it and name an existing file. Any symbolic link or junction below the root is rejected, so a link cannot
/// point a check at code outside the root. Same idea as the Rojo path guard in Bazaar.Server.Engines.
/// </summary>
internal static class LuauPathGuard
{
    private static readonly StringComparison _pathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Returns the normalised root. The root must exist; it may itself be a link (an operator choice).</summary>
    public static string RequireRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new LuauRequestRejectedException("Luau project root is not set.");
        }

        string full = FullPath(root);
        if (!Directory.Exists(full))
        {
            throw new LuauRequestRejectedException($"Luau project root '{full}' does not exist.");
        }

        return full;
    }

    /// <summary>
    /// Resolves each requested file under the root and returns it relative to the root, de-duplicated, in request order.
    /// Throws when the list is empty, a path is empty, escapes the root, passes through a link, or names no file.
    /// </summary>
    public static IReadOnlyList<string> ResolveFiles(string fullRoot, IReadOnlyList<string> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count == 0)
        {
            throw new LuauRequestRejectedException("No files were given to check.");
        }

        var relative = new List<string>(files.Count);
        foreach (string candidate in files)
        {
            string full = Contain(fullRoot, candidate);
            if (!File.Exists(full))
            {
                throw new LuauRequestRejectedException($"Luau check file '{candidate}' does not exist under the project root.");
            }

            // Forward slashes on every host: stable arguments for the tools and stable file names in diagnostics.
            string rel = Path.GetRelativePath(fullRoot, full).Replace(Path.DirectorySeparatorChar, '/');
            if (!relative.Contains(rel, StringComparer.Ordinal))
            {
                relative.Add(rel);
            }
        }

        return relative;
    }

    private static string Contain(string fullRoot, string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            throw new LuauRequestRejectedException("Luau check path is empty.");
        }

        string full = FullPath(Path.Combine(fullRoot, candidate));
        string prefix = fullRoot.EndsWith(Path.DirectorySeparatorChar) || fullRoot.EndsWith(Path.AltDirectorySeparatorChar)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;
        if (full.Length <= prefix.Length || !full.StartsWith(prefix, _pathComparison))
        {
            throw new LuauRequestRejectedException($"Luau check path '{candidate}' is outside the project root.");
        }

        RejectLinksBelowRoot(fullRoot, prefix, full);
        return full;
    }

    private static void RejectLinksBelowRoot(string fullRoot, string prefix, string full)
    {
        string current = fullRoot;
        foreach (string segment in full[prefix.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.Exists && info.LinkTarget is not null)
            {
                throw new LuauRequestRejectedException($"Luau check path '{full}' passes through a link below the project root.");
            }
        }
    }

    private static string FullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (ArgumentException ex)
        {
            throw new LuauRequestRejectedException($"Luau check path is invalid: {ex.Message}");
        }
        catch (NotSupportedException ex)
        {
            throw new LuauRequestRejectedException($"Luau check path is invalid: {ex.Message}");
        }
    }
}
