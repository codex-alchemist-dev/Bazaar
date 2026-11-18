namespace Bazaar.Server.Vms.Images;

/// <summary>
/// Seals a built parent image: records its SHA-256 and guest agent version, then sets the file read-only. After sealing,
/// the server must not write the parent while children exist; the verifier detects a parent that is writable again.
/// </summary>
public sealed class GoldenImageSealer(IImageFileSystem fileSystem, ImageHasher hasher)
{
    private readonly IImageFileSystem _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    private readonly ImageHasher _hasher = hasher ?? throw new ArgumentNullException(nameof(hasher));

    /// <summary>
    /// Seals the built image at <paramref name="path"/> for <paramref name="recipe"/>. Throws
    /// <see cref="FileNotFoundException"/> when there is no file to seal.
    /// </summary>
    public async Task<SealedImage> SealAsync(GoldenImageRecipe recipe, string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!_fileSystem.FileExists(path))
            throw new FileNotFoundException($"Cannot seal parent image '{path}': the file does not exist.", path);

        var sha256 = await _hasher.HashFileAsync(path, cancellationToken).ConfigureAwait(false);
        _fileSystem.SetReadOnly(path, isReadOnly: true);

        return new SealedImage(recipe.Os, recipe.Version, sha256, recipe.GuestAgentVersion, path);
    }
}
