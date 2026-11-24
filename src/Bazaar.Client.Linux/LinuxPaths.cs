namespace Bazaar.Client.Linux;

/// <summary>Where the Linux client keeps its per-user state, following the XDG base directory rules.</summary>
/// <param name="StateDirectory">The Bazaar state directory, such as <c>~/.local/state/bazaar</c>.</param>
public sealed record LinuxPaths(string StateDirectory)
{
    /// <summary>The replay cursor file.</summary>
    public string CursorFile => Path.Combine(StateDirectory, "cursors.json");

    /// <summary>The client settings file (theme, density, materials, motion level).</summary>
    public string SettingsFile => Path.Combine(StateDirectory, "client-settings.json");
    /// <summary>The last group chat the head opened and its server (doc 30), mode 0600.</summary>
    public string LastGcFile => Path.Combine(StateDirectory, "last-gc.json");

    /// <summary>The per-device preference file the pane host's preference store keeps (doc 41c section 6).</summary>
    public string PreferencesFile => Path.Combine(StateDirectory, "preferences.json");

    /// <summary>The person's mnemonic remaps (doc 41).</summary>
    public string MnemonicOverridesFile => Path.Combine(StateDirectory, "mnemonic-overrides.json");

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
        return new LinuxPaths(Path.Combine(root, "bazaar"));
    }
}
