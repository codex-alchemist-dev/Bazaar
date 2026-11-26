using System.Security.Cryptography;
using System.Text;
using Bazaar.Server.Identity.Kek;

namespace Bazaar.Server.SealedStorage;

/// <summary>
/// Checks a candidate KEK against the scope key headers of the sealed store (doc 61 Q2): <see cref="KekWrapCheck.Verified"/> when at least one
/// header unwraps, <see cref="KekWrapCheck.NoWrapsFound"/> when no header exists, and <see cref="KekWrapCheck.Failed"/> when headers exist and
/// none unwraps. It fails closed: a malformed, oversized or tampered header, or a scope folder whose name no <see cref="ISealedScopeNameSource"/>
/// knows, never counts as a match, so a start with only such headers is refused rather than the pre-migration copy wiped. Key material is
/// never logged or put in an exception message. The verifier does not depend on the store or the active KEK, so it can run while the KEK opens.
/// </summary>
public sealed class SealedKekWrapVerifier : IKekWrapVerifier
{
    private const string _headerFile = "scope.key";
    private const int _maxHeaderBytes = 4096;

    private readonly string _root;
    private readonly IReadOnlyList<ISealedScopeNameSource> _sources;

    public SealedKekWrapVerifier(string rootPath, IEnumerable<ISealedScopeNameSource> nameSources)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentNullException.ThrowIfNull(nameSources);
        _root = rootPath;
        _sources = [.. nameSources];
    }

    public async ValueTask<KekWrapCheck> CheckAsync(IKekProvider candidate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (!Directory.Exists(_root))
            return KekWrapCheck.NoWrapsFound;

        // Folder name (hash of the scope name) to the known scope name.
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var source in _sources)
        {
            foreach (var name in await source.ListScopeNamesAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    var scope = SealFormat.NormaliseScope(name);
                    names[FolderName(scope)] = scope;
                }
                catch (ArgumentException)
                {
                    // An invalid name cannot have a folder; ignore it.
                }
            }
        }

        var headers = 0;
        foreach (var directory in Directory.EnumerateDirectories(_root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var keyPath = Path.Combine(directory, _headerFile);
            if (!File.Exists(keyPath))
                continue;

            headers++;
            if (!names.TryGetValue(Path.GetFileName(directory), out var scope))
                continue;

            if (await UnwrapsAsync(candidate, scope, keyPath, cancellationToken).ConfigureAwait(false))
                return KekWrapCheck.Verified;
        }

        return headers == 0 ? KekWrapCheck.NoWrapsFound : KekWrapCheck.Failed;
    }

    private static string FolderName(string scope) =>
        Convert.ToHexString(SHA256.HashData(SealFormat.StrictUtf8.GetBytes(scope)))[..32];

    private static async ValueTask<bool> UnwrapsAsync(IKekProvider candidate, string scope, string keyPath, CancellationToken cancellationToken)
    {
        byte[]? bytes;
        try
        {
            bytes = await SealFormat.ReadBoundedAsync(keyPath, _maxHeaderBytes, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        if (bytes is null || bytes.Length < 6 || !bytes.AsSpan(0, 4).SequenceEqual(SealFormat.KeyMagic) || bytes[4] != SealFormat.Version || bytes.Length < 6 + bytes[5])
            return false;

        var kekId = Encoding.UTF8.GetString(bytes, 6, bytes[5]);
        if (!string.Equals(kekId, candidate.KekId, StringComparison.Ordinal))
            return false;

        byte[]? dek = null;
        try
        {
            dek = await candidate.UnwrapAsync(bytes.AsMemory(6 + bytes[5]), SealFormat.DekAad(scope, kekId), cancellationToken).ConfigureAwait(false);
            return dek.Length == SealFormat.KeyBytes;
        }
        catch (KekUnwrapException)
        {
            return false;
        }
        finally
        {
            if (dek is not null)
                CryptographicOperations.ZeroMemory(dek);
        }
    }
}
