namespace Bazaar.Server.Vms.Images;

/// <summary>Outcome of verifying a sealed parent image. <see cref="Ok"/> means every check passed.</summary>
public enum GoldenImageStatus
{
    Ok,
    Missing,
    HashMismatch,
    Writable,
    GuestAgentMismatch,
}

/// <summary>A verification outcome with a human-readable message.</summary>
public sealed record GoldenImageVerification(GoldenImageStatus Status, string Message)
{
    public bool IsOk => Status == GoldenImageStatus.Ok;
}

/// <summary>
/// Read-only check of a sealed parent (docs/design/16 "Golden image and rebase"). It never writes the file. Checks run in
/// order: the file exists, its SHA-256 matches the record, it is still read-only, and its guest agent version is the one
/// the server expects. The first failure is returned.
/// </summary>
public sealed class GoldenImageVerifier(IImageFileSystem fileSystem, ImageHasher hasher)
{
    private readonly IImageFileSystem _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    private readonly ImageHasher _hasher = hasher ?? throw new ArgumentNullException(nameof(hasher));

    /// <summary>Verifies a sealed parent against its record and the guest agent version the server requires.</summary>
    public async Task<GoldenImageVerification> VerifyAsync(
        SealedImage recorded,
        string expectedGuestAgentVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recorded);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedGuestAgentVersion);

        if (!_fileSystem.FileExists(recorded.Path))
            return new GoldenImageVerification(GoldenImageStatus.Missing, $"Parent image '{recorded.Path}' is missing.");

        var actualHash = await _hasher.HashFileAsync(recorded.Path, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(actualHash, recorded.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return new GoldenImageVerification(
                GoldenImageStatus.HashMismatch,
                $"Parent image '{recorded.Path}' has SHA-256 {actualHash}, expected {recorded.Sha256}.");
        }

        if (!_fileSystem.IsReadOnly(recorded.Path))
            return new GoldenImageVerification(GoldenImageStatus.Writable, $"Parent image '{recorded.Path}' is writable; a sealed parent must be read-only.");

        if (!string.Equals(recorded.GuestAgentVersion, expectedGuestAgentVersion, StringComparison.Ordinal))
        {
            return new GoldenImageVerification(
                GoldenImageStatus.GuestAgentMismatch,
                $"Parent image '{recorded.Path}' has guest agent {recorded.GuestAgentVersion}, the server requires {expectedGuestAgentVersion}.");
        }

        return new GoldenImageVerification(GoldenImageStatus.Ok, $"Parent image '{recorded.Path}' verified.");
    }
}
