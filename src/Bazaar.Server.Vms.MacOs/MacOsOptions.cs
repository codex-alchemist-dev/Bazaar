namespace Bazaar.Server.Vms.MacOs;

/// <summary>
/// Settings of the macOS VM provider (doc 62 section 7). The restore image pin comes from the <c>Bazaar:Vms:MacOs</c> configuration
/// section, not from settings: doc 62 names no setting for it.
/// </summary>
public sealed record MacOsOptions
{
    /// <summary>The pinned restore image id (the parent). Empty means not pinned, and the provider reports not usable.</summary>
    public string RestoreImageId { get; init; } = string.Empty;

    /// <summary>Apple's build number of the pinned image.</summary>
    public string RestoreImageBuild { get; init; } = string.Empty;

    /// <summary>Lower-case hex SHA-256 of the pinned image. Required with the id: a parent is pinned by content.</summary>
    public string RestoreImageSha256 { get; init; } = string.Empty;

    /// <summary>The golden image version id that stands for the pinned restore image. A child is accepted only from this parent.</summary>
    public required Guid RestoreImageVersion { get; init; }

    /// <summary><c>vms.macos.max_concurrent</c>. May lower Apple's cap of two, never raise it.</summary>
    public int MaxConcurrent { get; init; } = MacOsInstanceCap.HardCap;

    /// <summary>True when the id, build and hash are all set.</summary>
    public bool IsPinned => RestoreImageId.Length > 0 && RestoreImageBuild.Length > 0 && RestoreImageSha256.Length > 0;

    /// <summary>Throws when a pin part is malformed, only part of the pin is set, or the limit is out of range.</summary>
    public void Validate()
    {
        var any = RestoreImageId.Length > 0 || RestoreImageBuild.Length > 0 || RestoreImageSha256.Length > 0;
        if (any)
        {
            _ = new MacOsRestoreImagePin(RestoreImageId, RestoreImageBuild, RestoreImageSha256).Validated();
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(MaxConcurrent, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxConcurrent, MacOsInstanceCap.HardCap);
    }
}
