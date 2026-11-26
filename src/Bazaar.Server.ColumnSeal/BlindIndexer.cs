using System.Security.Cryptography;
using System.Text;

namespace Bazaar.Server.ColumnSeal;

/// <summary>
/// HMAC-SHA-256 blind index (doc 63 section 9, class C). The value is normalised with Unicode NFKC and then invariant
/// upper-case, so full-width, compatibility-form and upper/lower-case variants of one name index alike (the German sharp s stays itself: invariant upper-casing does not expand it); it is then encoded as UTF-8.
/// The HMAC key is derived from the scope's DEK with HKDF-SHA-256, with an info label naming scope, table and column and a salt of
/// none, so the same text gives different indexes in different scopes or columns and the DEK itself is never an HMAC key.
/// </summary>
public sealed class BlindIndexer(IColumnDekStore keys) : IBlindIndexer
{
    private const int _hmacKeySize = 32;

    /// <inheritdoc />
    public async ValueTask<BlindIndexValue> ComputeAsync(string scope, string table, string column, string value, CancellationToken cancellationToken)
    {
        Validate(scope, table, column, value);
        using var dek = await keys.GetOrCreateActiveAsync(scope, cancellationToken).ConfigureAwait(false);
        return Compute(dek, scope, table, column, value);
    }

    /// <inheritdoc />
    public async ValueTask<BlindIndexValue> ComputeAsync(string scope, string table, string column, string value, Guid keyId, CancellationToken cancellationToken)
    {
        Validate(scope, table, column, value);
        using var dek = await keys.GetAsync(keyId, cancellationToken).ConfigureAwait(false);
        if (dek is null || dek.Scope != scope)
            throw new ColumnSealException(ColumnSealFailure.KeyUnavailable, "The data key for this index is not available.");
        return Compute(dek, scope, table, column, value);
    }

    /// <summary>NFKC, then invariant upper-case. Public to the module so tests and feature code agree on the rule.</summary>
    internal static string Normalise(string value)
    {
        try
        {
            return value.Normalize(NormalizationForm.FormKC).ToUpperInvariant();
        }
        catch (ArgumentException)
        {
            throw ColumnSealNames.Invalid("value");
        }
    }

    private static void Validate(string scope, string table, string column, string value)
    {
        ColumnSealNames.ValidateName(scope, "scope");
        ColumnSealNames.ValidateName(table, "table name");
        ColumnSealNames.ValidateName(column, "column name");
        ArgumentNullException.ThrowIfNull(value);
    }

    private static BlindIndexValue Compute(ColumnDek dek, string scope, string table, string column, string value)
    {
        var info = ColumnSealNames.BuildBlindInfo(scope, table, column);
        var indexKey = new byte[_hmacKeySize];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, dek.Key, indexKey, salt: [], info: info);
        var normalised = ColumnSealNames.Utf8(Normalise(value));
        try
        {
            return new BlindIndexValue(dek.DekId, HMACSHA256.HashData(key: indexKey.AsSpan(), source: normalised.AsSpan()));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(indexKey);
            CryptographicOperations.ZeroMemory(normalised);
        }
    }
}
