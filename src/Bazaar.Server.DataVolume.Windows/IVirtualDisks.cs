namespace Bazaar.Server.DataVolume.Windows;

internal enum MountFolderState
{
    Missing,
    NotEmpty,
    Empty,
}

/// <summary>
/// Create, attach, detach and mount a VHDX (doc 63 section 3.1). Attaching and detaching need an administrator token.
/// Nothing here ever sees the volume key.
/// </summary>
internal interface IVirtualDisks
{
    bool ContainerExists(string vhdxPath);

    MountFolderState InspectMountFolder(string folder);

    /// <summary>Creates a thin (expandable) VHDX. It is not attached afterwards.</summary>
    ValueTask CreateAsync(string vhdxPath, long maxSizeBytes, CancellationToken cancellationToken);

    ValueTask<bool> IsAttachedAsync(string vhdxPath, CancellationToken cancellationToken);

    ValueTask AttachAsync(string vhdxPath, CancellationToken cancellationToken);

    /// <summary>On an attached, empty VHDX: GPT, one NTFS partition, mounted on <paramref name="folder"/> with no drive letter.</summary>
    ValueTask InitialiseAndMountAsync(string vhdxPath, string folder, CancellationToken cancellationToken);

    /// <summary>Removes the folder mount point (<c>mountvol &lt;folder&gt; /D</c>); the folder stays as an ordinary empty folder.</summary>
    ValueTask RemoveMountPointAsync(string folder, CancellationToken cancellationToken);

    ValueTask DetachAsync(string vhdxPath, CancellationToken cancellationToken);

    /// <summary>Deletes the container file when it exists.</summary>
    void DeleteContainer(string vhdxPath);
}
