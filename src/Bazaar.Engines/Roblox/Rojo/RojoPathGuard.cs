using System.IO;

namespace Bazaar.Engines.Roblox.Rojo;

/// <summary>
/// Keeps Rojo inputs and outputs inside one root directory. Paths are resolved against the root, normalised,
/// and must stay under it. Any symbolic link or junction along the path, below the root, is rejected, so a link
/// cannot point the build outside the root.
/// </summary>
internal static class RojoPathGuard
{
    private static readonly StringComparison _pathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Returns the normalised root. The root must exist; it may itself be a link (an operator choice).</summary>
    public static string RequireRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new RojoException(RojoErrorKind.ProjectNotFound, "Rojo root directory is not set.");
        }

        string full = FullPath(root);
        if (!Directory.Exists(full))
        {
            throw new RojoException(RojoErrorKind.ProjectNotFound, $"Rojo root directory '{full}' does not exist.");
        }

        return full;
    }

    /// <summary>Resolves an existing project file under the root.</summary>
    public static string ResolveProjectFile(string root, string projectPath)
    {
        string fullRoot = RequireRoot(root);
        string full = Contain(fullRoot, projectPath);
        if (!File.Exists(full))
        {
            throw new RojoException(RojoErrorKind.ProjectNotFound, $"Rojo project file '{full}' does not exist.");
        }

        return full;
    }

    /// <summary>
    /// Resolves an output file under the root. The file itself need not exist, but its directory must.
    /// </summary>
    public static string ResolveOutputFile(string root, string outputPath)
    {
        string fullRoot = RequireRoot(root);
        string full = Contain(fullRoot, outputPath);
        string? directory = Path.GetDirectoryName(full);
        if (directory is null || !Directory.Exists(directory))
        {
            throw new RojoException(RojoErrorKind.OutputDirectoryNotFound, $"Output directory for '{full}' does not exist.");
        }

        return full;
    }

    private static string Contain(string fullRoot, string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            throw new RojoException(RojoErrorKind.PathOutsideRoot, "Rojo path is empty.");
        }

        string full = FullPath(Path.Combine(fullRoot, candidate));
        string prefix = fullRoot.EndsWith(Path.DirectorySeparatorChar) || fullRoot.EndsWith(Path.AltDirectorySeparatorChar)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;
        if (full.Length <= prefix.Length || !full.StartsWith(prefix, _pathComparison))
        {
            throw new RojoException(RojoErrorKind.PathOutsideRoot, $"Rojo path '{candidate}' is outside root '{fullRoot}'.");
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
                throw new RojoException(RojoErrorKind.PathOutsideRoot, $"Rojo path '{full}' passes through a link at '{current}'.");
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
            throw new RojoException(RojoErrorKind.PathOutsideRoot, $"Rojo path is invalid: {ex.Message}");
        }
        catch (NotSupportedException ex)
        {
            throw new RojoException(RojoErrorKind.PathOutsideRoot, $"Rojo path is invalid: {ex.Message}");
        }
    }
}
