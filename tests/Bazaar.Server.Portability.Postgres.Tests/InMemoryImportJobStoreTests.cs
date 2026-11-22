using Bazaar.Server.Portability;

namespace Bazaar.Server.Portability.Postgres.Tests;

/// <summary>Runs the store contract against the in-memory fake. No database is needed.</summary>
public sealed class InMemoryImportJobStoreTests : ImportJobStoreContract
{
    protected override Task<StoreHandle> OpenAsync(CancellationToken ct) =>
        Task.FromResult(new StoreHandle(new InMemoryImportJobStore(), () => ValueTask.CompletedTask));
}
