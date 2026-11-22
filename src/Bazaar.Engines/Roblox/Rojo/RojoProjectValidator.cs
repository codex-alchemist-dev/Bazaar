using System.Text.Json;

namespace Bazaar.Engines.Roblox.Rojo;

/// <summary>
/// Checks the contents of a Rojo project file before rojo reads it. A project file is written by the agent (or taken from a
/// hostile repository) and names source folders with <c>$path</c>; rojo follows them wherever they lead, so a path that leaves the
/// root would read, and bundle into the build output, files the agent was never given. This validator parses the file with bounded
/// size and depth and refuses any <c>$path</c> that is absolute or resolves outside the root. Referenced folders are scanned for
/// links, and project files found inside them are checked the same way.
/// </summary>
/// <remarks>
/// Time of check to time of use: the files can change between this check and the moment rojo reads them. The check narrows the
/// window; it does not close it. The real boundary is that rojo runs with no secrets in its environment and, where the host can
/// provide it, inside the agent's sandbox.
/// </remarks>
internal static class RojoProjectValidator
{
    /// <summary>Largest project file read, in bytes.</summary>
    public const int MaxProjectFileBytes = 1024 * 1024;

    private const int _maxJsonDepth = 64;
    private const int _maxNestedProjects = 64;
    private const int _maxScannedEntries = 50_000;

    /// <summary>Validates <paramref name="projectFile"/> (already resolved under <paramref name="fullRoot"/>) and every project file it reaches.</summary>
    public static void Validate(string fullRoot, string projectFile)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        ValidateFile(fullRoot, projectFile, visited);
    }

    private static void ValidateFile(string fullRoot, string projectFile, HashSet<string> visited)
    {
        if (!visited.Add(projectFile))
        {
            return;
        }

        if (visited.Count > _maxNestedProjects)
        {
            throw Invalid($"Rojo project references more than {_maxNestedProjects} project files.");
        }

        var info = new FileInfo(projectFile);
        if (info.Length > MaxProjectFileBytes)
        {
            throw Invalid($"Rojo project file '{projectFile}' is larger than {MaxProjectFileBytes} bytes.");
        }

        JsonDocument document;
        try
        {
            using FileStream stream = File.OpenRead(projectFile);
            document = JsonDocument.Parse(
                stream,
                new JsonDocumentOptions { MaxDepth = _maxJsonDepth, CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException ex)
        {
            throw Invalid($"Rojo project file '{projectFile}' is not valid JSON: {ex.Message}");
        }

        using (document)
        {
            string projectDirectory = Path.GetDirectoryName(projectFile) ?? fullRoot;
            var pending = new Stack<JsonElement>();
            pending.Push(document.RootElement);
            while (pending.Count > 0)
            {
                JsonElement element = pending.Pop();
                switch (element.ValueKind)
                {
                    case JsonValueKind.Array:
                        foreach (JsonElement item in element.EnumerateArray())
                        {
                            pending.Push(item);
                        }

                        break;

                    case JsonValueKind.Object:
                        foreach (JsonProperty property in element.EnumerateObject())
                        {
                            if (property.NameEquals("$path"))
                            {
                                CheckPathValue(fullRoot, projectDirectory, property.Value, visited);
                            }
                            else
                            {
                                pending.Push(property.Value);
                            }
                        }

                        break;
                }
            }
        }
    }

    /// <summary><c>$path</c> is a string, or an object <c>{ "optional": "path" }</c>. Anything else is refused.</summary>
    private static void CheckPathValue(string fullRoot, string projectDirectory, JsonElement value, HashSet<string> visited)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                CheckPath(fullRoot, projectDirectory, value.GetString() ?? string.Empty, visited);
                return;

            case JsonValueKind.Object:
                bool sawOptional = false;
                foreach (JsonProperty property in value.EnumerateObject())
                {
                    if (!property.NameEquals("optional") || property.Value.ValueKind != JsonValueKind.String)
                    {
                        throw new RojoException(RojoErrorKind.PathOutsideRoot, "A Rojo $path object may hold only a string 'optional' path.");
                    }

                    sawOptional = true;
                    CheckPath(fullRoot, projectDirectory, property.Value.GetString() ?? string.Empty, visited);
                }

                if (!sawOptional)
                {
                    throw new RojoException(RojoErrorKind.PathOutsideRoot, "A Rojo $path object has no 'optional' path.");
                }

                return;

            default:
                throw new RojoException(RojoErrorKind.PathOutsideRoot, "A Rojo $path must be a string or an object with an 'optional' path.");
        }
    }

    private static void CheckPath(string fullRoot, string projectDirectory, string path, HashSet<string> visited)
    {
        if (string.IsNullOrWhiteSpace(path) || IsAbsoluteOnAnyOs(path))
        {
            throw new RojoException(RojoErrorKind.PathOutsideRoot, $"Rojo $path '{path}' must be a relative path.");
        }

        // Rojo resolves $path against the folder of the project file; the result must stay under the root.
        string relativeDirectory = Path.GetRelativePath(fullRoot, projectDirectory);
        string combined = Path.GetFullPath(Path.Combine(fullRoot, relativeDirectory, path));
        string resolved;
        if (string.Equals(combined.TrimEnd(Path.DirectorySeparatorChar), fullRoot.TrimEnd(Path.DirectorySeparatorChar), PathComparison))
        {
            // The root itself: nothing below it to check as a link.
            resolved = fullRoot;
        }
        else
        {
            resolved = RojoPathGuard.Contain(fullRoot, Path.GetRelativePath(fullRoot, combined));
        }

        if (File.Exists(resolved))
        {
            if (resolved.EndsWith(".project.json", StringComparison.OrdinalIgnoreCase))
            {
                ValidateFile(fullRoot, resolved, visited);
            }

            return;
        }

        if (Directory.Exists(resolved))
        {
            ScanDirectory(fullRoot, resolved, visited);
        }
    }

    /// <summary>Refuses a link anywhere under a referenced folder and validates nested project files.</summary>
    private static void ScanDirectory(string fullRoot, string directory, HashSet<string> visited)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false,
        };

        int count = 0;
        foreach (FileSystemInfo entry in new DirectoryInfo(directory).EnumerateFileSystemInfos("*", options))
        {
            if (++count > _maxScannedEntries)
            {
                throw Invalid($"Rojo source folder '{directory}' has more than {_maxScannedEntries} entries.");
            }

            if (entry.LinkTarget is not null)
            {
                throw new RojoException(RojoErrorKind.PathOutsideRoot, $"Rojo source folder contains a link at '{entry.FullName}'.");
            }

            if (entry is FileInfo && entry.Name.EndsWith(".project.json", StringComparison.OrdinalIgnoreCase))
            {
                ValidateFile(fullRoot, entry.FullName, visited);
            }
        }
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>True for a rooted path on either OS: a leading slash or backslash, or a drive prefix such as <c>C:</c>.</summary>
    private static bool IsAbsoluteOnAnyOs(string path) =>
        path[0] is '/' or '\\' || Path.IsPathRooted(path) || (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':');

    private static RojoException Invalid(string message) => new(RojoErrorKind.InvalidProjectFile, message);
}
