namespace Bazaar.Client.Linux;

/// <summary>Where the Linux client keeps its per-user state, following the XDG base directory rules.</summary>
/// <param name="StateDirectory">The Bazaar state directory, such as <c>~/.local/state/bazaar</c>.</param>
/// <param name="DataDirectory">The Bazaar data directory, such as <c>~/.local/share/bazaar</c>. Holds the installed releases.</param>
public sealed record LinuxPaths(string StateDirectory, string DataDirectory)
{
    /// <summary>The updater's install root: <c>releases/&lt;version&gt;</c>, <c>current</c> and <c>previous</c> live under it (doc 30 section 4).</summary>
    public string InstallRoot => Path.Combine(DataDirectory, "client");

    /// <summary>The replay cursor file.</summary>
    public string CursorFile => Path.Combine(StateDirectory, "cursors.json");

    /// <summary>
    /// Uses <c>$XDG_STATE_HOME</c> when it is an absolute path, as the XDG spec requires; otherwise
    /// <c>~/.local/state</c>. Pure: the environment and home directory are passed in.
    /// </summary>
    public static LinuxPaths FromEnvironment(Func<string, string?> env, string homeDirectory)
    {
        ArgumentNullException.ThrowIfNull(env);
        ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);

        string? xdgState = env("XDG_STATE_HOME");
        string root = !string.IsNullOrWhiteSpace(xdgState) && Path.IsPathRooted(xdgState)
            ? xdgState
            : Path.Combine(homeDirectory, ".local", "state");
        string? xdgData = env("XDG_DATA_HOME");
        string dataRoot = !string.IsNullOrWhiteSpace(xdgData) && Path.IsPathRooted(xdgData)
            ? xdgData
            : Path.Combine(homeDirectory, ".local", "share");
        return new LinuxPaths(Path.Combine(root, "bazaar"), Path.Combine(dataRoot, "bazaar"));
    }
}
