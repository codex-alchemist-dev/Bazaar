using System.IO;

namespace Bazaar.Server.Engines.Roblox.Rojo;

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

    internal static string Contain(string fullRoot, string candidate)
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

        RejectUnsafeSegments(candidate, full[prefix.Length..]);
        RejectLinksBelowRoot(fullRoot, prefix, full);
        return full;
    }

    /// <summary>
    /// Refuses names a filesystem would read differently from how this guard did: control characters (NUL and newline), a colon
    /// (an alternate data stream or drive-relative path on Windows), a trailing dot or space (Windows drops them silently), and
    /// Windows device names such as NUL or COM1. Applied on every OS so a project behaves the same everywhere.
    /// </summary>
    private static void RejectUnsafeSegments(string candidate, string relative)
    {
        if (candidate.Any(char.IsControl))
        {
            throw new RojoException(RojoErrorKind.PathOutsideRoot, "Rojo path contains a control character.");
        }

        // The path as given is checked for trailing dots and spaces as well: on Windows the path normaliser has already
        // dropped them from the resolved form, so only the given form still shows them. A rooted path keeps its drive colon,
        // so the colon rule applies to the resolved part below the root only.
        foreach (string segment in candidate.Split('/', '\\'))
        {
            if (segment is not ("" or "." or "..") && (segment.EndsWith('.') || segment.EndsWith(' ')))
            {
                throw new RojoException(RojoErrorKind.PathOutsideRoot, $"Rojo path segment '{segment}' is not a safe file name.");
            }
        }

        foreach (string segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (segment.Contains(':', StringComparison.Ordinal)
                || segment.EndsWith('.')
                || segment.EndsWith(' ')
                || IsReservedDeviceName(segment))
            {
                throw new RojoException(RojoErrorKind.PathOutsideRoot, $"Rojo path segment '{segment}' is not a safe file name.");
            }
        }
    }

    private static bool IsReservedDeviceName(string segment)
    {
        int dot = segment.IndexOf('.', StringComparison.Ordinal);
        string stem = (dot < 0 ? segment : segment[..dot]).TrimEnd();
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return stem.Length == 4
            && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
            && stem[3] is >= '0' and <= '9';
    }

    internal static void RejectLinksBelowRoot(string fullRoot, string prefix, string full)
    {
        string current = fullRoot;
        foreach (string segment in full[prefix.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            // LinkTarget is read whether or not the target exists: a dangling link at an output path would otherwise pass
            // and let the build write through it to wherever it points.
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.LinkTarget is not null)
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
