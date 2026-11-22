namespace Bazaar.Server.VmMounts.Abstractions;

/// <summary>Guest paths the server chooses for one guest OS. A mount request must name exactly these.</summary>
public sealed record GuestMountLayout(string SoulPath, string WorkspacePath, string ProfilePath)
{
    public static GuestMountLayout DefaultLinux { get; } = new("/mnt/bazaar/soul", "/mnt/bazaar/workspace", "/var/lib/bazaar/profile");

    public static GuestMountLayout DefaultWindows { get; } = new(@"C:\Bazaar\Soul", @"C:\Bazaar\Workspace", @"C:\Bazaar\Profile");
}

/// <summary>
/// The single source of truth for the guest-side paths of the scoped mounts and the cookie profile working copy. Other modules (the
/// browser profiles, the workspace facade) read it instead of repeating the paths.
/// </summary>
public interface IGuestMountLayoutProvider
{
    GuestMountLayout GetLayout(GuestOs os);
}
