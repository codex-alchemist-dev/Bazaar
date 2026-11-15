using Bazaar.Client.Core.Pairing;

namespace Bazaar.Client.Android.Pairing;

/// <summary>
/// Keeps the sealed refresh credential in one file in app-private storage. Writes go to a temporary file first and then
/// replace the old file, so a crash mid-write leaves the previous credential in place.
/// </summary>
public sealed class AppPrivateBlobStore(string path) : ISealedBlobStore
{
    private readonly string _path = !string.IsNullOrWhiteSpace(path)
        ? path
        : throw new ArgumentException("A file path is required.", nameof(path));

    public async Task<byte[]?> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        return await File.ReadAllBytesAsync(_path, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteAsync(byte[] blob, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(blob);
        var temporary = _path + ".tmp";
        await File.WriteAllBytesAsync(temporary, blob, cancellationToken).ConfigureAwait(false);
        File.Move(temporary, _path, overwrite: true);
    }

    public Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }

        return Task.CompletedTask;
    }
}
