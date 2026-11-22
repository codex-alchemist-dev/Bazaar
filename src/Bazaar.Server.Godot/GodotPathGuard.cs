using System.IO;
using Bazaar.Server.Godot.Abstractions;

namespace Bazaar.Server.Godot;

/// <summary>
/// Keeps Godot command paths inside one project root. Each path is resolved against the root, normalised, and must stay under
/// it. Any symbolic link or junction below the root is rejected, so a link cannot point a command at files outside the root.
/// Paths handed to Godot are relative to the root and use forward slashes on every OS.
/// </summary>
internal static class GodotPathGuard
{
    private const string ResPrefix = "res://";

    private static readonly StringComparison _pathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Returns the normalised root. The root must exist and hold <c>project.godot</c>; it may itself be a link (an operator choice).</summary>
    public static string RequireProjectRoot(string root)
    {
        string full = RequireDirectory(root);
        if (!File.Exists(Path.Combine(full, "project.godot")))
        {
            throw new GodotRequestRejectedException($"Godot project root '{full}' has no project.godot.");
        }

        return full;
    }

    /// <summary>Returns the normalised root without requiring <c>project.godot</c> (used by the build check).</summary>
    public static string RequireDirectory(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new GodotRequestRejectedException("Godot project root is not set.");
        }

        string full = FullPath(root);
        if (!Directory.Exists(full))
        {
            throw new GodotRequestRejectedException($"Godot project root '{full}' does not exist.");
        }

        return full;
    }

    /// <summary>Resolves an existing file under the root and returns it relative to the root with forward slashes.</summary>
    public static string ResolveFile(string fullRoot, string candidate)
    {
        string full = Contain(fullRoot, candidate);
        if (!File.Exists(full))
        {
            throw new GodotRequestRejectedException($"Godot path '{candidate}' is not an existing file under the project root.");
        }

        return ToArgument(fullRoot, full);
    }

    /// <summary>Resolves an existing directory under the root (never the root itself) and returns it relative with forward slashes.</summary>
    public static string ResolveDirectory(string fullRoot, string candidate)
    {
        string full = Contain(fullRoot, candidate);
        if (!Directory.Exists(full))
        {
            throw new GodotRequestRejectedException($"Godot path '{candidate}' is not an existing directory under the project root.");
        }

        return ToArgument(fullRoot, full);
    }

    private static string ToArgument(string fullRoot, string full)
    {
        string relative = Path.GetRelativePath(fullRoot, full).Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');

        // A leading dash would be read by Godot as a flag.
        if (relative.StartsWith('-'))
        {
            throw new GodotRequestRejectedException($"Godot path '{relative}' starts with a dash.");
        }

        return relative;
    }

    private static string Contain(string fullRoot, string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            throw new GodotRequestRejectedException("Godot path is empty.");
        }

        // res:// is the project root in Godot's own notation; accept it as a project-relative path.
        string relative = candidate.StartsWith(ResPrefix, StringComparison.Ordinal) ? candidate[ResPrefix.Length..] : candidate;
        if (relative.Length == 0 || relative.Contains('\0'))
        {
            throw new GodotRequestRejectedException("Godot path is empty or invalid.");
        }

        string full = FullPath(Path.Combine(fullRoot, relative));
        string prefix = fullRoot.EndsWith(Path.DirectorySeparatorChar) || fullRoot.EndsWith(Path.AltDirectorySeparatorChar)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;
        if (full.Length <= prefix.Length || !full.StartsWith(prefix, _pathComparison))
        {
            throw new GodotRequestRejectedException($"Godot path '{candidate}' is outside the project root.");
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
                throw new GodotRequestRejectedException($"Godot path '{full}' passes through a link below the project root.");
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
            throw new GodotRequestRejectedException($"Godot path is invalid: {ex.Message}");
        }
        catch (NotSupportedException ex)
        {
            throw new GodotRequestRejectedException($"Godot path is invalid: {ex.Message}");
        }
    }
}
