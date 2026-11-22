using Bazaar.Server.Browser.Abstractions;
using Bazaar.Server.SealedStorage;

namespace Bazaar.Server.SealedPassport;

/// <summary>
/// One-time import of an existing plaintext Chromium profile folder into an account profile's sealed <c>browserdir:&lt;profile-id&gt;</c> scope
/// (doc 61 WP4, using the WP7 <see cref="SealedImporter"/>: each file is sealed, read back and compared before its plaintext is zeroed and
/// deleted). Owner-started and audited. Anonymous profiles are refused: they are never sealed, so their plaintext is not imported.
/// Files the store already has are kept in plaintext and reported; links are never followed or imported and stay where they are.
/// </summary>
public sealed class SealedPassportImporter(ISealedStore store, SealedPassportStore passport) : ISealedPassportImporter
{
    /// <inheritdoc />
    public async Task<PassportImportReport> ImportProfileDirectoryAsync(Guid profileId, string sourceDirectory, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        var profile = await passport.FindProfileAsync(profileId, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException("Browser profile not found.");
        if (profile.Class != BrowserProfileClass.Account)
        {
            throw new InvalidOperationException("Only account profiles are sealed; an anonymous profile's plaintext folder is not imported.");
        }

        var source = Path.GetFullPath(sourceDirectory);
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException("The plaintext profile folder does not exist.");
        }

        using var scope = await store.OpenScopeAsync(PassportScopes.Directory(profileId), ct).ConfigureAwait(false);
        using var lease = await scope.AcquireLeaseAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        var report = await passport.GuardAsync(profileId, async () => await SealedImporter.ImportDirectoryAsync(scope, source, removePlaintext: true, ct).ConfigureAwait(false), ct).ConfigureAwait(false);

        var removed = false;
        if (!Directory.EnumerateFileSystemEntries(source, "*", SearchOption.AllDirectories).Any(e => File.Exists(e) || ProfileFolderWipe.IsLink(new FileInfo(e))))
        {
            // Only empty folders remain: every file was sealed, verified and zero-wiped.
            Directory.Delete(source, recursive: true);
            removed = true;
        }

        await passport.RecordAsync(
            "passport.import",
            scope.Scope,
            null,
            $"Imported {report.Imported}, kept {report.SkippedExisting} already sealed, removed {report.RemovedPlaintext.Count} plaintext; source {(removed ? "removed" : "kept (files remain)")}.",
            ct).ConfigureAwait(false);
        return new PassportImportReport(profileId, report.Imported, report.SkippedExisting, report.RemovedPlaintext.Count, removed);
    }
}
