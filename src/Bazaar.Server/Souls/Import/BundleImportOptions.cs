namespace Bazaar.Server.Souls.Import;

/// <summary>
/// Size caps for a Soul bundle import (docs/design/07-soul-system.md, "Import"). The defaults are the safe shipped values;
/// all three must be positive. Caps are checked on the bytes actually received, not on the sizes the manifest claims.
/// </summary>
public sealed record BundleImportOptions
{
    /// <summary>Largest accepted <c>manifest.json</c>, in bytes. Checked before parsing.</summary>
    public long MaxManifestBytes { get; init; } = 1024 * 1024;

    /// <summary>Largest accepted single bundle file, in bytes.</summary>
    public long MaxFileBytes { get; init; } = 16 * 1024 * 1024;

    /// <summary>Largest accepted total of all bundle file bytes (the manifest is counted separately).</summary>
    public long MaxTotalBytes { get; init; } = 64 * 1024 * 1024;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxManifestBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxFileBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxTotalBytes);
    }
}
