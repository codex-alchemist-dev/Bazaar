using System.Security.Cryptography;

namespace Bazaar.Server.SealedStorage;

/// <summary>Result of <see cref="SealedImporter.ImportDirectoryAsync(ISealedScope, string, bool, CancellationToken)"/>.</summary>
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
    public static ValueTask<SealImportReport> ImportDirectoryAsync(
        ISealedScope scope,
        string sourceDirectory,
        bool removePlaintext,
        CancellationToken cancellationToken = default) =>
        ImportDirectoryAsync(scope, sourceDirectory, removePlaintext, include: null, cancellationToken);

    /// <summary>
    /// As the overload without <paramref name="include"/>, importing only the files whose logical path <paramref name="include"/> accepts; the others
    /// are neither imported nor removed. Links are never followed or imported. Plaintext is removed through <see cref="SecureFileWipe"/> (no-follow
    /// handle, hard links deleted without zeroing), and every buffer read is zeroed after use.
    /// </summary>
    public static async ValueTask<SealImportReport> ImportDirectoryAsync(
        ISealedScope scope,
        string sourceDirectory,
        bool removePlaintext,
        Func<string, bool>? include,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var root = Path.GetFullPath(sourceDirectory);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException(root);
        }

        if (SecureFileWipe.IsLink(root))
        {
            throw new InvalidOperationException("The import source is a link; it is not followed.");
        }

        var imported = 0;
        var skipped = 0;
        var removed = new List<string>();
        var (files, _, _) = SecureFileWipe.Walk(root);
        foreach (var file in files.Select(f => f.FullName).Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var logical = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (include is not null && !include(logical))
            {
                continue;
            }

            var bytes = await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false);
            SealedFile? back = null;
            try
            {
                try
                {
                    await scope.WriteAsync(logical, bytes, 0, cancellationToken).ConfigureAwait(false);
                }
                catch (SealGenerationConflictException)
                {
                    skipped++;
                    continue;
                }

                back = await scope.ReadAsync(logical, cancellationToken).ConfigureAwait(false);
                if (back is null || !CryptographicOperations.FixedTimeEquals(back.Content, bytes))
                {
                    throw new SealTamperedException(scope.Scope, logical, "Imported file did not read back identically; plaintext kept.");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
                if (back is not null)
                {
                    CryptographicOperations.ZeroMemory(back.Content);
                }
            }

            imported++;
            if (removePlaintext)
            {
                SecureFileWipe.ZeroAndDelete(file, root);
                removed.Add(logical);
            }
        }

        return new SealImportReport(imported, skipped, removed);
    }
}
