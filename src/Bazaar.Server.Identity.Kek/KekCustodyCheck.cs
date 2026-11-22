using Npgsql;

namespace Bazaar.Server.Identity.Kek;

/// <summary>
/// Walks the live wrapped keys of every store and stops at the first one the host KEK opens. A database with no wrapped key
/// passes (a first start). When keys exist and none opens, the KEK is not this database's. The walk reads every row only in that
/// failing case: an interrupted switch of <c>identity.kek_provider</c> leaves the lowest ids under the new key and the rest under
/// the old one, so a sample of the first rows alone would refuse a host that is still configured for the old key.
/// </summary>
/// <remarks>
/// The KEK and the stores are opened on the first check, not when the check is built: the host builds it before it holds the
/// instance lock, and opening the database or the key file there would turn an unreachable database into a crash.
/// </remarks>
public sealed class KekCustodyCheck(Func<IKekProvider> openKek, Func<IReadOnlyList<IKekRewrapStore>> openStores) : IKekCustodyCheck
{
    public KekCustodyCheck(IKekProvider kek, IReadOnlyList<IKekRewrapStore> stores)
        : this(() => kek, () => stores)
    {
    }

    private const int _pageSize = 256;

    // undefined_table: the component that owns the table is not installed in this database, so it holds no keys of that kind.
    private const string _undefinedTable = "42P01";

    /// <inheritdoc />
    public async Task<KekCustodyResult> CheckAsync(CancellationToken cancellationToken)
    {
        // Opened at the first stored key: a database with none never needs the key file.
        IKekProvider? kek = null;
        var stored = 0;
        foreach (var store in openStores())
        {
            Guid? after = null;
            while (true)
            {
                IReadOnlyList<KekWrapRecord> page;
                try
                {
                    page = await store.ListLiveWrapsAsync(after, _pageSize, cancellationToken).ConfigureAwait(false);
                }
                catch (PostgresException ex) when (ex.SqlState == _undefinedTable)
                {
                    break;
                }

                foreach (var row in page)
                {
                    stored++;
                    if (await OpensAsync(kek ??= openKek(), row, cancellationToken).ConfigureAwait(false))
                    {
                        return new KekCustodyResult(stored, 1);
                    }
                }

                if (page.Count < _pageSize)
                {
                    break;
                }

                after = page[^1].Id;
            }
        }

        return new KekCustodyResult(stored, 0);
    }

    private static async ValueTask<bool> OpensAsync(IKekProvider kek, KekWrapRecord row, CancellationToken cancellationToken)
    {
        try
        {
            var key = await kek.UnwrapAsync(row.Wrapped, row.AssociatedData, cancellationToken).ConfigureAwait(false);
            Array.Clear(key);
            return true;
        }
        catch (KekUnwrapException)
        {
            return false;
        }
    }
}
