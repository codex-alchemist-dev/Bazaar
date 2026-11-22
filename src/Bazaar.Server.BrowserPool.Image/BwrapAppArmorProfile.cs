namespace Bazaar.Server.BrowserPool.Image;

/// <summary>
/// The AppArmor profile that lets <c>bwrap</c> create unprivileged user namespaces on Ubuntu 24.04, which restricts them by default
/// (docs/design/59, section 11, limit 6). It is the standard shape: an unconfined profile for the one binary with the <c>userns</c>
/// permission. Only <c>bwrap</c> gets it; every other program stays under the restriction. The path is the only value put into the text.
/// </summary>
public static class BwrapAppArmorProfile
{
    /// <summary>The profile name as AppArmor lists it once loaded.</summary>
    public const string ProfileName = "bazaar-pool-bwrap";

    /// <summary>The file name to install the profile under in <c>/etc/apparmor.d/</c>.</summary>
    public const string InstallFileName = "bazaar-pool-bwrap";

    /// <summary>The directory the profile file is installed into.</summary>
    public const string InstallDirectory = "/etc/apparmor.d";

    /// <summary>The full install path.</summary>
    public const string InstallPath = InstallDirectory + "/" + InstallFileName;

    /// <summary>Renders the profile for <paramref name="bwrapPath"/>.</summary>
    /// <exception cref="ArgumentException">The path is not a safe absolute path.</exception>
    public static string Render(string bwrapPath)
    {
        if (!BrowserPoolOptionsValidator.IsSafeAbsolutePath(bwrapPath))
        {
            throw new ArgumentException("The bwrap path is not a safe absolute path.", nameof(bwrapPath));
        }

        return $$"""
            abi <abi/4.0>,
            include <tunables/global>

            profile {{ProfileName}} {{bwrapPath}} flags=(unconfined) {
              userns,

              # Site-specific additions go in /etc/apparmor.d/local/{{ProfileName}}.
              include if exists <local/{{ProfileName}}>
            }

            """.ReplaceLineEndings("\n");
    }
}
