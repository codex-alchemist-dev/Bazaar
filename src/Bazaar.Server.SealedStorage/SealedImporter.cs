using System.Security.Cryptography;

namespace Bazaar.Server.SealedStorage;

/// <summary>Result of <see cref="SealedImporter.ImportDirectoryAsync"/>.</summary>
public sealed record SealImportReport(int Imported, int SkippedExisting, IReadOnlyList<string> RemovedPlaintext);

/// <summary>
/// One-time migration of a plaintext folder into a sealed scope (doc 61 work package 7). Each file is written,
/// read back and compared before its plaintext original is removed, so a failure never loses data.
/// </summary>
public static class SealedImporter
{
    /// <summary>
    /// Imports every file under <paramref name="sourceDirectory"/>. Existing sealed paths are never overwritten.
    /// When <paramref name="removePlaintext"/> is true, a plaintext file is overwritten with zeros and deleted only after its sealed copy verified.
    /// </summary>
    public static async ValueTask<SealImportReport> ImportDirectoryAsync(
        ISealedScope scope,
        string sourceDirectory,
        bool removePlaintext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var root = Path.GetFullPath(sourceDirectory);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException(root);
        }

        var imported = 0;
        var skipped = 0;
        var removed = new List<string>();
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var file in Directory.EnumerateFiles(root, "*", options).Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var logical = Path.GetRelativePath(root, file).Replace('\\', '/');
            var bytes = await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false);
            try
            {
                await scope.WriteAsync(logical, bytes, 0, cancellationToken).ConfigureAwait(false);
            }
            catch (SealGenerationConflictException)
            {
                skipped++;
                continue;
            }

            var back = await scope.ReadAsync(logical, cancellationToken).ConfigureAwait(false);
            if (back is null || !CryptographicOperations.FixedTimeEquals(back.Content, bytes))
            {
                throw new SealTamperedException(scope.Scope, logical, "Imported file did not read back identically; plaintext kept.");
            }

            imported++;
            if (removePlaintext)
            {
                await File.WriteAllBytesAsync(file, new byte[bytes.Length], cancellationToken).ConfigureAwait(false);
                File.Delete(file);
                removed.Add(logical);
            }
        }

        return new SealImportReport(imported, skipped, removed);
    }
}
