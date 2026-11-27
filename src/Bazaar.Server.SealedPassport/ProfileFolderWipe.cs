using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Bazaar.Server.SealedStorage;

namespace Bazaar.Server.SealedPassport;

/// <summary>
/// Folder helpers for plaintext profile folders. Wiping goes through <see cref="SecureFileWipe"/>: nothing follows a link, files are zeroed through a
/// no-follow handle with their identity re-checked, and a file with several hard links is deleted without zeroing.
/// </summary>
internal static class ProfileFolderWipe
{
    /// <summary>Zero-wipes and removes the tree. Returns the first failure (a refused link root included), or null.</summary>
    public static Exception? WipeAndDelete(string dir)
    {
        try
        {
            return SecureFileWipe.WipeTree(dir);
        }
        catch (InvalidOperationException ex)
        {
            return ex;
        }
    }

    /// <summary>Creates a folder only the current user can enter (Unix mode 0700 at creation; on Windows an explicit owner-only DACL for the root, inherited below it).</summary>
    public static void CreateOwnerOnly(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            CreateOwnerOnlyWindows(path);
        }
        else
        {
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void CreateOwnerOnlyWindows(string path)
    {
        if (Directory.Exists(path))
        {
            return;
        }

        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path));
        if (parent is not null && !Directory.Exists(parent))
        {
            Directory.CreateDirectory(parent);
        }

        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in RootAcl.PermittedSids())
        {
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(sid), FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        }

        new DirectoryInfo(path).Create(security);
    }
}

/// <summary>The Windows half of the mount-root floor: the root's DACL must grant access to nobody but the service account, SYSTEM and Administrators.</summary>
internal static class RootAcl
{
    private const string _system = "S-1-5-18";
    private const string _administrators = "S-1-5-32-544";
    private const string _creatorOwner = "S-1-3-0";

    /// <summary>True when every allow rule names a permitted principal. Pure, so the rule is testable on any OS.</summary>
    public static bool IsOwnerOnly(IEnumerable<(string Sid, bool Allow)> rules, IReadOnlySet<string> permitted, out string? offender)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(permitted);
        foreach (var (sid, allow) in rules)
        {
            if (allow && !permitted.Contains(sid))
            {
                offender = sid;
                return false;
            }
        }

        offender = null;
        return true;
    }

    [SupportedOSPlatform("windows")]
    public static IReadOnlyList<string> PermittedSids() =>
        [WindowsIdentity.GetCurrent().User!.Value, _system, _administrators];

    /// <summary>Null when the root's DACL is owner-only, else the reason it is not.</summary>
    [SupportedOSPlatform("windows")]
    public static string? Check(string root)
    {
        var security = new DirectoryInfo(root).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        var permitted = new HashSet<string>(PermittedSids(), StringComparer.Ordinal) { _creatorOwner };
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Select(r => (r.IdentityReference.Value, r.AccessControlType == AccessControlType.Allow));
        if (!IsOwnerOnly(rules, permitted, out var offender))
        {
            return $"The mount root's DACL grants access to {offender}; only the service account, SYSTEM and Administrators may have it.";
        }

        return security.GetOwner(typeof(SecurityIdentifier))?.Value is { } owner && permitted.Contains(owner)
            ? null
            : "The mount root is owned by an account other than the service account, SYSTEM or Administrators.";
    }
}
