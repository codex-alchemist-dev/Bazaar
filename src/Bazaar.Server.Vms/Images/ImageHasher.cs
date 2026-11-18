using System.Security.Cryptography;

namespace Bazaar.Server.Vms.Images;

/// <summary>Streaming SHA-256 of image files. The file is read in chunks, never loaded whole.</summary>
public sealed class ImageHasher(IImageFileSystem fileSystem)
{
    private readonly IImageFileSystem _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));

    /// <summary>Returns the lowercase hex SHA-256 of the file's contents.</summary>
    public async Task<string> HashFileAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        await using var stream = _fileSystem.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }
}
