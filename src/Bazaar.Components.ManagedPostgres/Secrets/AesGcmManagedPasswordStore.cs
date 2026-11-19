using System.Security.Cryptography;
using System.Text;

namespace Bazaar.Components.ManagedPostgres.Secrets;

/// <summary>
/// Stores the superuser password encrypted with AES-256-GCM. The file holds nonce, tag and ciphertext only; the plaintext
/// password is never written to disk. The key comes from <see cref="IManagedStoreKeyProvider"/>.
/// </summary>
public sealed class AesGcmManagedPasswordStore(string path, IManagedStoreKeyProvider keys) : IManagedPasswordStore
{
    private const int _keySize = 32;
    private const int _nonceSize = 12;
    private const int _tagSize = 16;

    public async Task<string?> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var blob = await File.ReadAllBytesAsync(path, cancellationToken);
        if (blob.Length < _nonceSize + _tagSize)
        {
            throw new InvalidDataException("The managed password store is truncated.");
        }

        var key = await LoadKeyAsync(cancellationToken);
        var nonce = blob.AsSpan(0, _nonceSize);
        var tag = blob.AsSpan(_nonceSize, _tagSize);
        var cipher = blob.AsSpan(_nonceSize + _tagSize);
        var plain = new byte[cipher.Length];

        try
        {
            using var aes = new AesGcm(key, _tagSize);
            aes.Decrypt(nonce, cipher, tag, plain);
        }
        catch (CryptographicException)
        {
            // Wrong key or tampered file. The message never includes the content.
            throw new InvalidDataException("The managed password store could not be decrypted.");
        }

        return Encoding.UTF8.GetString(plain);
    }

    public async Task SaveAsync(string password, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);

        var key = await LoadKeyAsync(cancellationToken);
        var nonce = RandomNumberGenerator.GetBytes(_nonceSize);
        var plain = Encoding.UTF8.GetBytes(password);
        var cipher = new byte[plain.Length];
        var tag = new byte[_tagSize];

        using (var aes = new AesGcm(key, _tagSize))
        {
            aes.Encrypt(nonce, plain, cipher, tag);
        }

        CryptographicOperations.ZeroMemory(plain);

        var blob = new byte[_nonceSize + _tagSize + cipher.Length];
        nonce.CopyTo(blob, 0);
        tag.CopyTo(blob, _nonceSize);
        cipher.CopyTo(blob, _nonceSize + _tagSize);

        // Write to a private temp name beside the target, flush it to disk, then move it into place. A unique name keeps two
        // saves from sharing one temp file, the flush keeps a power cut from leaving a torn store behind a finished rename,
        // and the owner-only mode is set at creation so the file is never readable by others, even briefly.
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            await using (var stream = new FileStream(temp, options))
            {
                await stream.WriteAsync(blob, cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temp);
            }
            catch (IOException)
            {
                // Best effort: a leftover temp name is unique and never read.
            }
        }
    }

    private async Task<byte[]> LoadKeyAsync(CancellationToken cancellationToken)
    {
        var key = await keys.GetKeyAsync(cancellationToken);
        if (key.Length != _keySize)
        {
            throw new InvalidOperationException("The managed store key must be 32 bytes.");
        }

        return key;
    }
}
