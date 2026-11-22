namespace Bazaar.Server.VmMounts.Abstractions;

/// <summary>
/// Reports how many directory entries point at one file. A file with more than one link inside a mount tree can be a hard link to
/// something outside it, which a path check cannot see. .NET has no portable API for this, so the OS provider supplies it
/// (<c>GetFileInformationByHandle</c> on Windows, <c>stat</c> on Linux). The shipped default returns null (not detectable).
/// </summary>
public interface IHardLinkProbe
{
    /// <summary>The link count of a regular file, or null when this host cannot tell.</summary>
    int? GetLinkCount(string filePath);
}
