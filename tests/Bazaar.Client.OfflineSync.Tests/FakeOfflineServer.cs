using Bazaar.Client.OfflineSync;
using Bazaar.OfflineSync;

namespace Bazaar.Client.OfflineSync.Tests;

/// <summary>
/// A fake server for the replay driver. It applies writes in device sequence order, answers duplicates, reports gaps, and can fail
/// on one chosen send (before or after it applies the batch, which is a lost request or a lost response).
/// </summary>
public sealed class FakeOfflineServer : IOfflineSyncTransport
{
    /// <summary>Write ids in the order they were applied. Each id appears once when the server is correct.</summary>
    public List<Guid> AppliedLog { get; } = [];

    /// <summary>The highest device sequence applied.</summary>
    public long Acked { get; set; }

    /// <summary>How many sends the server has received.</summary>
    public int Sends { get; private set; }

    /// <summary>The 1-based send number that fails, or null.</summary>
    public int? FaultSend { get; set; }

    /// <summary>True: the failing send applies the batch first, then the response is lost. False: the request is lost before the server sees it.</summary>
    public bool FaultAfterApply { get; set; }

    /// <summary>The 1-based send number on which the batch arrives without its second write (a gap), or null.</summary>
    public int? DropWriteAtSend { get; set; }

    /// <summary>When true, the server acknowledges nothing and applies nothing (a broken contract).</summary>
    public bool Stale { get; set; }

    /// <summary>Outcome kinds forced for specific writes. Forced writes are recorded but not applied.</summary>
    public Dictionary<Guid, OfflineOutcomeKind> Overrides { get; } = [];

    /// <inheritdoc />
    public Task<OfflineBatchResult> SendAsync(OfflineWriteBatch batch, CancellationToken cancellationToken)
    {
        Sends++;
        var fault = Sends == FaultSend;
        if (fault && !FaultAfterApply)
        {
            throw new IOException("The link dropped before the server received the batch.");
        }

        var writes = batch.Writes.ToList();
        if (Sends == DropWriteAtSend && writes.Count > 1)
        {
            writes.RemoveAt(1);
        }

        var outcomes = new List<OfflineWriteOutcome>();
        long? gap = null;
        if (!Stale)
        {
            foreach (var write in writes)
            {
                if (write.DeviceSequence <= Acked)
                {
                    outcomes.Add(new OfflineWriteOutcome(write.WriteId, OfflineOutcomeKind.Duplicate));
                    continue;
                }

                if (write.DeviceSequence != Acked + 1)
                {
                    gap = Acked + 1;
                    break;
                }

                if (Overrides.TryGetValue(write.WriteId, out var kind))
                {
                    outcomes.Add(new OfflineWriteOutcome(write.WriteId, kind));
                }
                else
                {
                    AppliedLog.Add(write.WriteId);
                    outcomes.Add(new OfflineWriteOutcome(write.WriteId, OfflineOutcomeKind.Applied));
                }

                Acked = write.DeviceSequence;
            }
        }

        if (fault)
        {
            throw new IOException("The link dropped after the server applied the batch; the response was lost.");
        }

        return Task.FromResult(new OfflineBatchResult(outcomes, Acked, gap));
    }
}
