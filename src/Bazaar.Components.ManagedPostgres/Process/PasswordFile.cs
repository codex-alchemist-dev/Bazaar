using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace Bazaar.Components.ManagedPostgres;

/// <summary>
/// The short-lived file initdb reads the superuser password from (<c>--pwfile</c>). Created readable only by the
/// current user (mode 0600 on Unix, a protected ACL on Windows), written before initdb runs, and deleted afterwards.
/// A process killed between create and delete leaves the file behind, so <see cref="SweepStale"/> removes old ones.
/// </summary>
internal static class PasswordFile
{
    internal const string Prefix = "bazaar-pg-";
    internal const string Suffix = ".pwfile";

    /// <summary>Files older than this are considered abandoned. initdb takes seconds, so an hour is far past any live run.</summary>
    internal static readonly TimeSpan StaleAfter = TimeSpan.FromHours(1);

    /// <summary>Creates a new password file in the temp directory and returns its path.</summary>
    internal static string Create(string password)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Prefix}{Guid.NewGuid():N}{Suffix}");

        // Create the empty file with the restricted mode first, so no content exists until access is restricted.
        var createOptions = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        if (!OperatingSystem.IsWindows())
        {
            createOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (new FileStream(path, createOptions))
        {
        }

        if (OperatingSystem.IsWindows())
        {
            RestrictToCurrentUser(path);
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(password);
        writer.Write('\n');
        return path;
    }

    /// <summary>Deletes password files left behind by killed runs. Returns how many were removed.</summary>
    internal static int SweepStale(DateTime utcNow)
    {
        var removed = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(Path.GetTempPath(), Prefix + "*" + Suffix))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < utcNow - StaleAfter)
                    {
                        File.Delete(file);
                        removed++;
                    }
                }
                catch (IOException)
                {
                    // Locked or already gone; a later sweep will try again.
                }
                catch (UnauthorizedAccessException)
                {
                    // Not ours to remove; leave it.
                }
            }
        }
        catch (IOException)
        {
            // The temp directory could not be listed; nothing to sweep this time.
        }
        catch (UnauthorizedAccessException)
        {
            // The temp directory could not be listed; nothing to sweep this time.
        }

        return removed;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void RestrictToCurrentUser(string path)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }
}
