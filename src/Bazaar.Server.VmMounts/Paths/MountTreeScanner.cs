using Bazaar.Server.VmMounts.Abstractions;
using Bazaar.Server.VmMounts.Canaries;

namespace Bazaar.Server.VmMounts.Paths;

/// <summary>
/// Walks a tree that is about to be shared and refuses it when it holds something the share must not expose: a file the content
/// policy refuses, a link that leaves the tree, or more entries than the scan limit (a tree too big to check is not mounted).
/// A link that stays inside the tree is allowed but never followed, so a loop cannot trap the scan. A file with more than one hard
/// link is refused where the host can tell (<see cref="IHardLinkProbe"/>).
/// </summary>
/// <remarks>
/// The scan is advisory. It is a point-in-time look at a tree that, for a workspace, the guest can change the next moment, and it
/// cannot see hard links on a host with no probe. The control that holds is the provider's rule never to follow a link out of the
/// share (<see cref="IMountProvider"/>); the scan only stops an obviously bad tree from being offered at all.
/// </remarks>
internal static class MountTreeScanner
{
    public static void Scan(string mountRoot, MountKind kind, int maxEntries, bool caseInsensitive, IMountContentPolicy policy, IHardLinkProbe linkProbe)
    {
        var realRoot = HostPathGuard.RealPath(mountRoot, 0);
        var pending = new Stack<string>();
        pending.Push(mountRoot);
        var count = 0;

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (++count > maxEntries)
                    throw new MountRefusedException(
                        MountRefusalReason.ContentRefused, $"The mount tree has more than {maxEntries} entries and cannot be checked.");

                var name = Path.GetFileName(entry);
                if (directory == mountRoot && name == CanaryRegistry.FolderName)
                    continue;

                var relative = Path.GetRelativePath(mountRoot, entry).Replace('\\', '/');
                if (HostPathGuard.IsLink(entry))
                {
                    FileSystemInfo info = Directory.Exists(entry) ? new DirectoryInfo(entry) : new FileInfo(entry);
                    var target = info.ResolveLinkTarget(returnFinalTarget: true);
                    if (target is null || !SafeNames.IsUnderOrEqual(realRoot, HostPathGuard.RealPath(target.FullName, 0), caseInsensitive))
                        throw new MountRefusedException(
                            MountRefusalReason.LinkNotAllowed, $"'{SafeText.Clean(relative)}' is a link that leads out of the mount tree.");
                    Judge(policy, kind, relative, 0);
                    continue;
                }

                var isDirectory = Directory.Exists(entry);
                if (!isDirectory && linkProbe.GetLinkCount(entry) is > 1)
                    throw new MountRefusedException(
                        MountRefusalReason.LinkNotAllowed, $"'{SafeText.Clean(relative)}' has more than one hard link.");
                Judge(policy, kind, relative, isDirectory ? 0 : new FileInfo(entry).Length);
                if (isDirectory)
                    pending.Push(entry);
            }
        }
    }

    private static void Judge(IMountContentPolicy policy, MountKind kind, string relative, long length)
    {
        var decision = policy.Evaluate(new MountContentCandidate(kind, MountContentOrigin.MountTree, relative, length));
        if (!decision.Allowed)
            throw new MountRefusedException(MountRefusalReason.ContentRefused, $"'{SafeText.Clean(relative)}' is refused: {SafeText.Clean(decision.Reason)}.");
    }
}
