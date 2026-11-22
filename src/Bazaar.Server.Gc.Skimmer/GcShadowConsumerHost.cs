using System.Threading.Channels;
using Bazaar.Server.Gc.Wire;
using Bazaar.Server.Skimmer.Shadow;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Bazaar.Server.Gc.Skimmer;

/// <summary>What the host passes the shadow consumers: the gate settings and the two limits (doc 13, "Settings").</summary>
/// <param name="Settings">The gate settings (<c>skimmer.mode</c> and the rest).</param>
/// <param name="QueueDepthLimit"><c>skimmer.queue_depth_limit</c>: above this many pending messages the consumer uses the rules only.</param>
/// <param name="BatchSize">How many messages one run decides.</param>
public sealed record GcShadowConsumerOptions(ShadowSettings Settings, int QueueDepthLimit = 200, int BatchSize = 50);

/// <summary>
/// Hosts the shadow consumers on GC notifications (doc 13, "Placement"): a commit's NOTIFY queues its GC, and one worker runs
/// the per-agent <see cref="ShadowConsumer"/> for every agent that is an active member of that GC. Bursts coalesce, because the
/// consumer reads from its cursor and decides everything pending. After a listener reconnect, any notification may have been
/// missed, so every GC an agent belongs to is queued once. A failure is logged and the GC is picked up by the next
/// notification, since the cursor is the highest recorded seq. It never wakes or enqueues anything.
/// </summary>
public sealed class GcShadowConsumerHost(
    IGcNotificationSource notifications,
    IShadowAgentDirectory directory,
    ShadowConsumer consumer,
    NpgsqlDataSource dataSource,
    GcShadowConsumerOptions options,
    ILogger<GcShadowConsumerHost> logger) : BackgroundService
{
    private const string _membersSql = """
        SELECT principal_id FROM gc_member_current WHERE gc_id = $1 AND left_seq IS NULL AND principal_id = ANY($2)
        """;

    private const string _gcsSql = """
        SELECT DISTINCT gc_id FROM gc_member_current WHERE principal_id = ANY($1) AND left_seq IS NULL
        """;

    private readonly Channel<Guid> _queue = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });
    private readonly HashSet<Guid> _pending = [];
    private readonly Lock _gate = new();

    /// <summary>Queues a GC for one pass. A GC already waiting is not queued twice.</summary>
    public void Enqueue(Guid gcId)
    {
        lock (_gate)
        {
            if (!_pending.Add(gcId))
                return;
        }

        _queue.Writer.TryWrite(gcId);
    }

    /// <summary>Runs every queued pass until <paramref name="ct"/> is cancelled. Exposed so a test can drive the worker.</summary>
    public async Task RunWorkerAsync(CancellationToken ct)
    {
        await foreach (var gcId in _queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            lock (_gate)
                _pending.Remove(gcId);
            await RunGcAsync(gcId, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Runs one pass over a GC: every active member agent decides its pending messages.</summary>
    public async Task<int> RunGcAsync(Guid gcId, CancellationToken ct)
    {
        var recorded = 0;
        try
        {
            var agents = await directory.ListAsync(ct).ConfigureAwait(false);
            var members = await MemberAgentsAsync(gcId, [.. agents.Select(a => a.Agent.AgentId)], ct).ConfigureAwait(false);
            foreach (var entry in agents.Where(a => members.Contains(a.Agent.AgentId)))
            {
                recorded += await consumer
                    .RunOnceAsync(gcId, entry.Agent, options.Settings, options.QueueDepthLimit, options.BatchSize, ct)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Shadow pass for GC {GcId} failed; the next notification retries from the cursor.", gcId);
        }

        return recorded;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var worker = RunWorkerAsync(stoppingToken);
        try
        {
            await foreach (var n in notifications.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                if (n.Reconnected)
                    await EnqueueAgentGcsAsync(stoppingToken).ConfigureAwait(false);
                else
                    Enqueue(n.GcId);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            _queue.Writer.TryComplete();
            try
            {
                await worker.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private async Task EnqueueAgentGcsAsync(CancellationToken ct)
    {
        var agents = await directory.ListAsync(ct).ConfigureAwait(false);
        await using var cmd = dataSource.CreateCommand(_gcsSql);
        cmd.Parameters.Add(new NpgsqlParameter<Guid[]> { TypedValue = [.. agents.Select(a => a.Agent.AgentId)] });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            Enqueue(reader.GetGuid(0));
    }

    private async Task<HashSet<Guid>> MemberAgentsAsync(Guid gcId, Guid[] agentIds, CancellationToken ct)
    {
        var members = new HashSet<Guid>();
        if (agentIds.Length == 0)
            return members;

        await using var cmd = dataSource.CreateCommand(_membersSql);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = gcId });
        cmd.Parameters.Add(new NpgsqlParameter<Guid[]> { TypedValue = agentIds });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            members.Add(reader.GetGuid(0));
        return members;
    }
}
