namespace Bazaar.Client.Calcinato.Pty;

/// <summary>
/// Picks the shell to start (doc 52, section 5.3): the spec shell, then the <c>SHELL</c> variable, then
/// <c>pwsh</c> or <c>cmd</c> on Windows and <c>bash</c> or <c>sh</c> on Linux, searching <c>PATH</c>.
/// </summary>
public static class ShellResolver
{
    /// <summary>
    /// Returns the full path of the shell to start.
    /// </summary>
    /// <param name="requested">The spec shell, a path or a name. Null picks the default.</param>
    /// <param name="isWindows">The target OS. Selects the default candidates and the <c>.exe</c> suffix rule.</param>
    /// <param name="getEnvironment">Reads an environment variable. Null reads the process environment.</param>
    /// <param name="findOnPath">Maps a program name to a full path or null. Null searches <c>PATH</c> on disk.</param>
    /// <returns>The full path of an existing shell.</returns>
    /// <exception cref="InvalidOperationException">No shell was found. A requested shell that does not exist also throws.</exception>
    public static string Resolve(
        string? requested,
        bool isWindows,
        Func<string, string?>? getEnvironment = null,
        Func<string, string?>? findOnPath = null)
    {
        getEnvironment ??= static name => Environment.GetEnvironmentVariable(name);
        findOnPath ??= name => SearchPath(name, isWindows, getEnvironment);

        if (!string.IsNullOrWhiteSpace(requested))
        {
            // An explicit choice never falls back silently: a missing shell is an error.
            return FindShell(requested.Trim(), findOnPath)
                ?? throw new InvalidOperationException($"The requested shell '{requested}' was not found.");
        }

        var fromEnvironment = getEnvironment("SHELL");
        if (!string.IsNullOrWhiteSpace(fromEnvironment) && FindShell(fromEnvironment.Trim(), findOnPath) is { } fromShell)
        {
            return fromShell;
        }

        var candidates = isWindows ? new[] { "pwsh", "cmd" } : new[] { "bash", "sh" };
        foreach (var candidate in candidates)
        {
            if (findOnPath(candidate) is { } found)
            {
                return found;
            }
        }

        if (isWindows && findOnPath("cmd.exe") is { } cmdExe)
        {
            return cmdExe;
        }

        throw new InvalidOperationException("No usable shell was found on this system.");
    }

    private static string? FindShell(string nameOrPath, Func<string, string?> findOnPath)
    {
        // A path with a directory part is taken as is, if it exists.
        if (nameOrPath.Contains(Path.DirectorySeparatorChar) || nameOrPath.Contains(Path.AltDirectorySeparatorChar))
        {
            return File.Exists(nameOrPath) ? Path.GetFullPath(nameOrPath) : null;
        }

        return findOnPath(nameOrPath);
    }

    private static string? SearchPath(string name, bool isWindows, Func<string, string?> getEnvironment)
    {
        var path = getEnvironment("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var names = new List<string> { name };
        if (isWindows && Path.GetExtension(name).Length == 0)
        {
            names.Insert(0, name + ".exe");
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var candidate in names)
            {
                var full = Path.Combine(directory, candidate);
                if (File.Exists(full))
                {
                    return full;
                }
            }
        }

        return null;
    }
}
