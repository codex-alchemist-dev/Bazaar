using Bazaar.Server.Gateway.Policy;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.Gateway.SystemPolicy;

/// <summary>
/// Reconciles <c>agent_model_policy</c> rows for the declared system agents (doc 05, "System-agent policy seeding"). It creates
/// a missing row and refreshes a row whose <c>changed_by</c> is still <see cref="ChangedBy"/>. A row any other principal wrote
/// is never overwritten. Runs at host start; call <see cref="ReconcileAsync"/> again after a setting a source read changes.
/// </summary>
public sealed partial class SystemAgentPolicySeeder(
    IEnumerable<ISystemAgentPolicySource> sources,
    ISystemPolicyStore store,
    ILogger<SystemAgentPolicySeeder> logger) : IHostedService, IDisposable
{
    /// <summary>The changed-by label on rows this seeder owns.</summary>
    public const string ChangedBy = "system-seed";

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Reconciles every source once. Returns the number of rows written.</summary>
    public async Task<int> ReconcileAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var written = 0;
            foreach (var source in sources)
            {
                try
                {
                    if (await ReconcileOneAsync(source, cancellationToken).ConfigureAwait(false))
                    {
                        written++;
                    }
                }
                catch (PolicyFormatException ex)
                {
                    LogRejected(logger, source.AgentId, ex.Message);
                }
            }

            return written;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> ReconcileOneAsync(ISystemAgentPolicySource source, CancellationToken cancellationToken)
    {
        var declaration = await source.DeclareAsync(cancellationToken).ConfigureAwait(false);
        if (declaration is null)
        {
            return false;
        }

        // Validate first so a bad declaration never reaches the store, whatever the store does.
        _ = AgentModelPolicy.Create(declaration.AgentId, declaration.Entries, declaration.DefaultRoute, 1);

        var row = await store.GetRowAsync(declaration.AgentId, cancellationToken).ConfigureAwait(false);
        if (row is not null)
        {
            if (!string.Equals(row.ChangedBy, ChangedBy, StringComparison.Ordinal))
            {
                LogOperatorOwned(logger, declaration.AgentId, row.ChangedBy);
                return false;
            }

            if (string.Equals(row.DefaultRoute, declaration.DefaultRoute, StringComparison.Ordinal)
                && row.Allowed.SequenceEqual(declaration.Entries, StringComparer.Ordinal))
            {
                return false;
            }
        }

        await store.SaveAsync(declaration.AgentId, declaration.Entries, declaration.DefaultRoute, ChangedBy, cancellationToken).ConfigureAwait(false);
        var listed = string.Join(", ", declaration.Entries);
        LogSeeded(logger, declaration.AgentId, listed);
        return true;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken) =>
        _ = await ReconcileAsync(cancellationToken).ConfigureAwait(false);

    public void Dispose() => _gate.Dispose();

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "System policy seed: {AgentId} may use {Entries}.")]
    private static partial void LogSeeded(ILogger logger, string agentId, string entries);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "System policy seed: leaving {AgentId} alone, its row was changed by {ChangedBy}.")]
    private static partial void LogOperatorOwned(ILogger logger, string agentId, string changedBy);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "System policy seed: the declaration for {AgentId} was rejected: {Reason}")]
    private static partial void LogRejected(ILogger logger, string agentId, string reason);
}
