using System.Security.Cryptography;

namespace Bazaar.Server.Backup.Restore;

/// <summary>Computes the size and SHA-256 of a backup set file, the pair that <see cref="IRestoreBackupSource"/> reports.</summary>
public static class BackupSetDigest
{
    /// <summary>Streams the file and returns its lowercase hex SHA-256 and size, or null when it does not exist.</summary>
    public static async Task<(string Sha256Hex, long SizeBytes)?> DescribeFileAsync(string path, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return (Convert.ToHexStringLower(hash), stream.Length);
    }
}
