using System.Security.Cryptography;
using Bazaar.Server.Identity.Kek;

namespace Bazaar.Server.ColumnSeal.Tests;

/// <summary>Shared test setup: a real file KEK in a throwaway folder (the repo's own provider, no fake crypto) and a sealer over an in-memory store.</summary>
internal sealed class SealFixture : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "colseal-" + Guid.NewGuid().ToString("N"));

    public SealFixture(ColumnDekStoreOptions? options = null)
    {
        Kek = FileKekProvider.Open(Path.Combine(_root, "keys", "kek.bin"));
        Store = new InMemoryColumnDekStore(Kek, options);
        Sealer = new ColumnSealer(Store);
        Indexer = new BlindIndexer(Store);
    }

    public FileKekProvider Kek { get; }

    public InMemoryColumnDekStore Store { get; }

    public ColumnSealer Sealer { get; }

    public BlindIndexer Indexer { get; }

    public static ColumnSealContext Context(string row = "row-1", string scope = "gc", string table = "gc_event", string column = "payload") =>
        new(table, column, row, scope);

    /// <summary>Random bytes, so no secret-shaped literal sits in the source.</summary>
    public static byte[] Random(int length = 48) => RandomNumberGenerator.GetBytes(length);

    public void Dispose()
    {
        Store.Dispose();
        Kek.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp folder is harmless.
        }
    }
}
