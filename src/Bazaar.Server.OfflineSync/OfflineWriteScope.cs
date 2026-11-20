using Bazaar.OfflineSync;

namespace Bazaar.Server.OfflineSync;

/// <summary>
/// A unit of work around the merge of one write: the rule's state changes, the claim of the write id, the outcome record and the
/// merge audit commit together or not at all (doc 56 section 8, item 4).
/// </summary>
public interface IOfflineWriteScope : IAsyncDisposable
{
    /// <summary>Commits everything done under the scope. Disposing a scope that was not committed rolls it back.</summary>
    /// <param name="cancellationToken">Cancels the commit.</param>
    ValueTask CommitAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Opens a <see cref="IOfflineWriteScope"/> for one write. A persistent merge store implements this so that merges per
/// <see cref="OfflineWrite.Target"/> are serialized (the Kanban edge cycle check relies on it). The scope must be established
/// synchronously, so that code that runs inside the processor's call chain can find it (for example through an
/// <see cref="AsyncLocal{T}"/>); it may open its connection lazily.
/// </summary>
public interface IOfflineWriteScopeFactory
{
    /// <summary>Begins a scope for <paramref name="write"/>.</summary>
    /// <param name="write">The write about to be merged.</param>
    /// <returns>The scope. The caller commits it and disposes it.</returns>
    IOfflineWriteScope Begin(OfflineWrite write);
}

/// <summary>The default scope factory: no transaction, nothing to serialize. For the in-memory store only.</summary>
public sealed class NoOpOfflineWriteScopeFactory : IOfflineWriteScopeFactory
{
    /// <inheritdoc />
    public IOfflineWriteScope Begin(OfflineWrite write) => NoOpScope.Instance;

    private sealed class NoOpScope : IOfflineWriteScope
    {
        public static readonly NoOpScope Instance = new();

        public ValueTask CommitAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
