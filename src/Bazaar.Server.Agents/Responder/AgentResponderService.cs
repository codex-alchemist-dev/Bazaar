using System.Threading.Channels;
using Bazaar.Server.Wire.Sync;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.Agents.Responder;

/// <summary>
/// The hosted loop that feeds <see cref="AgentResponder"/>. It reads the GC notify stream (<see cref="IGcNotificationSource"/>,
/// the Postgres <c>LISTEN gc</c> path) and gives each GC its own lane: a queue drained by one task, so GCs run in parallel and
/// each GC runs one pass at a time. A notification is only a hint, and the responder reads the log from its own cursor, so a
/// hint that is coalesced or lost costs latency, not a missed message.
/// </summary>
/// <remarks>
/// After a reconnect the source sends a marker. Every lane then gets a pass with no upper bound, which reads whatever committed
/// while the connection was down. The lanes are created on demand and live until the host stops.
/// </remarks>
public sealed partial class AgentResponderService(
    IGcNotificationSource notifications,
    AgentResponder responder,
    ILogger<AgentResponderService> logger) : BackgroundService
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Channel<long>> _lanes = [];
    private readonly ILogger<AgentResponderService> _logger = logger;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var notification in notifications.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                if (notification.Reconnected)
                {
                    WakeEveryLane();
                    continue;
                }

                if (notification.GcId == Guid.Empty)
                    continue;

                LaneFor(notification.GcId, notification.Seq, stoppingToken).Writer.TryWrite(notification.Seq);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private Channel<long> LaneFor(Guid gcId, long firstSeenSeq, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_lanes.TryGetValue(gcId, out var lane))
                return lane;

            responder.Observe(gcId, firstSeenSeq);
            lane = Channel.CreateUnbounded<long>(new UnboundedChannelOptions { SingleReader = true });
            _lanes.Add(gcId, lane);
            _ = RunLaneAsync(gcId, lane.Reader, ct);
            return lane;
        }
    }

    private void WakeEveryLane()
    {
        lock (_gate)
        {
            // long.MaxValue means "everything the log has": the responder reads only the messages past its cursor.
            foreach (var lane in _lanes.Values)
                lane.Writer.TryWrite(long.MaxValue);
        }
    }

    private async Task RunLaneAsync(Guid gcId, ChannelReader<long> reader, CancellationToken ct)
    {
        try
        {
            while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                // Hints that queued up while the last pass ran collapse into one pass up to the newest seq.
                var through = 0L;
                while (reader.TryRead(out var seq))
                    through = Math.Max(through, seq);

                try
                {
                    await responder.ProcessAsync(gcId, through, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    LogLaneFailed(gcId, ex);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Agent responder: a pass over GC {GcId} failed. The next hint for the GC tries again.")]
    private partial void LogLaneFailed(Guid gcId, Exception ex);
}
