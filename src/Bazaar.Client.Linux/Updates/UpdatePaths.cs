namespace Bazaar.Client.Linux.Updates;

/// <summary>Where the Linux updater keeps releases and finds the pinned signing key (XDG data and config homes).</summary>
/// <param name="InstallRoot">The updater's install root: <c>releases/&lt;version&gt;</c>, <c>current</c> and <c>previous</c> live under it (doc 30 section 4).</param>
/// <param name="DefaultKeyFile">The default place of the pinned OpenPGP public key.</param>
public sealed record UpdatePaths(string InstallRoot, string DefaultKeyFile)
{
    /// <summary>Uses <c>$XDG_DATA_HOME</c> and <c>$XDG_CONFIG_HOME</c> when absolute, as the XDG spec requires. Pure.</summary>
    public static UpdatePaths FromEnvironment(Func<string, string?> env, string homeDirectory)
    {
        ArgumentNullException.ThrowIfNull(env);
        ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);

        string data = Absolute(env("XDG_DATA_HOME")) ?? Path.Combine(homeDirectory, ".local", "share");
        string config = Absolute(env("XDG_CONFIG_HOME")) ?? Path.Combine(homeDirectory, ".config");
        return new UpdatePaths(Path.Combine(data, "bazaar", "client"), Path.Combine(config, "bazaar", "update-key.asc"));
    }

    private static string? Absolute(string? value) =>
        !string.IsNullOrWhiteSpace(value) && Path.IsPathRooted(value) ? value : null;
}
