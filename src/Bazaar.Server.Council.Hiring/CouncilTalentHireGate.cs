using System.Collections.Concurrent;
using Bazaar.Server.Council.Service;

namespace Bazaar.Server.Council.Hiring;

/// <summary>
/// Opens Talent hire votes on the Council and reports their quarantine state (docs/design/25-company-systems-knowledge.md,
/// work package 8). The vote decides quarantine release. Unanimity, follow-up and the owner escalation stay in the Council.
/// </summary>
/// <remarks>
/// Decision guard: <see cref="RequestAsync"/> serialises per template id with a keyed lock, then re-reads the
/// <see cref="IHireDecisionStore"/> inside it. Concurrent requests for one template therefore cannot both see "no open vote"
/// and open two hire votes. Requests for different templates do not wait on each other. The host's store must be safe to call
/// from several templates at once. The lock is per process, so a multi-instance host needs a store-level guard as well.
/// </remarks>
public sealed class CouncilTalentHireGate(
    ICouncilService council,
    IHireDecisionStore store,
    HireMotionDraftBuilder builder,
    TimeProvider time) : IHireGate
{
    private readonly ConcurrentDictionary<string, KeyedLock> _templateLocks = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public async Task<HireVoteState> RequestAsync(HireRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var entry = RetainLock(request.TemplateId);
        try
        {
            await entry.Gate.WaitAsync(ct);
            try
            {
                // One open vote per template: a pending or released template returns its current state without a new vote.
                var current = await store.FindAsync(request.TemplateId, ct);
                if (current is not null && current.Status is HireStatus.Pending or HireStatus.Released)
                {
                    return current;
                }

                var motion = builder.Build(request);
                var voteId = await council.OpenAsync(request.ProposerId, motion.Draft, ct);
                var pending = new HireVoteState(request.TemplateId, voteId, HireStatus.Pending, Decision: null, time.GetUtcNow());
                await store.SaveAsync(pending, ct);
                return pending;
            }
            finally
            {
                entry.Gate.Release();
            }
        }
        finally
        {
            ReleaseLock(request.TemplateId, entry);
        }
    }

    /// <inheritdoc />
    public async Task<bool> IsReleasedAsync(string templateId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templateId);

        var current = await store.FindAsync(templateId, ct);
        return current?.Status == HireStatus.Released;
    }

    /// <summary>Gets or creates the lock for a template and counts this caller as a user, so the entry is not retired meanwhile.</summary>
    private KeyedLock RetainLock(string templateId)
    {
        while (true)
        {
            var entry = _templateLocks.GetOrAdd(templateId, static _ => new KeyedLock());
            lock (entry)
            {
                if (entry.Retired)
                {
                    // A releaser retired this entry between GetOrAdd and here. Fetch a fresh one.
                    continue;
                }

                entry.Users++;
                return entry;
            }
        }
    }

    /// <summary>Drops the caller's use of a template lock, and removes the entry once no caller holds or waits for it.</summary>
    private void ReleaseLock(string templateId, KeyedLock entry)
    {
        lock (entry)
        {
            entry.Users--;
            if (entry.Users == 0)
            {
                entry.Retired = true;
                _templateLocks.TryRemove(new KeyValuePair<string, KeyedLock>(templateId, entry));
            }
        }
    }

    /// <summary>A per-template gate. Users and Retired are guarded by the instance lock.</summary>
    private sealed class KeyedLock
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public int Users { get; set; }

        public bool Retired { get; set; }
    }
}
