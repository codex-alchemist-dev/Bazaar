using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bazaar.Server.Embeddings.Indexing;

/// <summary>What one sweep did. <see cref="Idle"/> is true when no profile is active (setting <c>none</c>), so nothing was read or sent.</summary>
/// <remarks>
/// <see cref="TooLong"/> counts blocks the model refused as too long; they are skipped (never truncated) and retried on the next sweep.
/// <see cref="RetryLater"/> counts the blocks in batches a provider deferred. During a planned switch the counts add up both profiles;
/// <see cref="IEmbeddingStatus"/> keeps them apart. <see cref="Activated"/> is true when this sweep swapped the active profile.
/// </remarks>
public sealed record IndexRunResult(bool Idle, int Embedded, int SkippedPersonal, string? Error, int TooLong = 0, int RetryLater = 0, bool Activated = false)
{
    public static IndexRunResult IdleResult { get; } = new(true, 0, 0, null);
}

/// <summary>
/// One sweep over the Vault blocks that lack a current embedding under the active profile (doc 08, "WP6 detail"). Personal-data blocks
/// are skipped and never reach a provider. A provider failure ends the sweep with an error and writes nothing for that batch; the next
/// sweep retries it, and lexical search is unaffected. When a provider reports an input as too long, the batch is embedded block by block
/// so only the offending blocks are skipped.
/// </summary>
/// <remarks>
/// With a <see cref="ProfileSwitchPlanner"/> the sweep also backfills a target profile and activates it once its coverage reaches
/// <c>memory.embedding_switch_threshold</c> (doc 08, "Planned profile switch" [PROPOSED]). Logs carry counts and ids, never block text.
/// </remarks>
public sealed partial class EmbeddingIndexer(
    IActiveEmbeddingSelector selector,
    IEmbeddingIndexStore store,
    IPersonalDataBlockDetector personalData,
    int batchSize = 32,
    ProfileSwitchPlanner? planner = null,
    IEmbeddingProfileStore? profiles = null,
    IEmbeddingSwitchSettingSource? thresholdSource = null,
    EmbeddingStatusTracker? status = null,
    ILogger<EmbeddingIndexer>? logger = null,
    TimeProvider? time = null)
{
    private readonly ILogger _log = logger ?? NullLogger<EmbeddingIndexer>.Instance;
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<(Guid Profile, Guid Doc, int Ordinal, string Hash), byte> _tooLongLogged = [];

    private sealed record Sweep(int Embedded, int SkippedPersonal, int TooLong, int RetryLater, string? Error);

    public async Task<IndexRunResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        ActiveEmbedding? active = null;
        string? activeError = null;
        try
        {
            active = await selector.ResolveAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (EmbeddingException ex)
        {
            activeError = ex.Message;
        }

        ActiveEmbedding? target = null;
        string? targetError = null;
        if (planner is not null && profiles is not null)
        {
            try
            {
                target = await planner.ResolveTargetAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (EmbeddingException ex)
            {
                targetError = ex.Message;
            }
        }

        if (active is null && target is null)
        {
            if (activeError is null && targetError is null)
            {
                status?.Publish(EmbeddingStatusSnapshot.Initial with { LastActivatedAt = status.LastActivatedAt });
                return IndexRunResult.IdleResult;
            }

            return new IndexRunResult(Idle: false, 0, 0, activeError ?? targetError);
        }

        var now = _time.GetUtcNow();
        Sweep? activeSweep = active is null ? null : await SweepAsync(active, cancellationToken).ConfigureAwait(false);
        Sweep? targetSweep = target is null ? null : await SweepAsync(target, cancellationToken).ConfigureAwait(false);
        var activeCoverage = active is null ? null : await CoverageAsync(active.Profile.ProfileId, activeSweep!, cancellationToken).ConfigureAwait(false);
        var targetCoverage = target is null ? null : await CoverageAsync(target.Profile.ProfileId, targetSweep!, cancellationToken).ConfigureAwait(false);

        var threshold = thresholdSource is null
            ? EmbeddingSettings.SwitchThresholdKey.Default
            : await thresholdSource.GetSwitchThresholdAsync(cancellationToken).ConfigureAwait(false);
        var activated = false;
        if (target is not null && targetSweep!.Error is null && targetCoverage!.Reaches(threshold))
        {
            // One transaction in the store: the old profile stops being active in the same step the new one starts.
            await profiles!.ActivateAsync(target.Profile.ProfileId, cancellationToken).ConfigureAwait(false);
            activated = true;
            LogActivated(target.Profile.ProfileId, targetCoverage.Embedded, targetCoverage.Embeddable);
        }
        else if (target is not null)
        {
            LogSwitchProgress(target.Profile.ProfileId, targetCoverage!.Embedded, targetCoverage.Embeddable, threshold);
        }

        if (status is not null)
        {
            EmbeddingSweepStatus? Report(ActiveEmbedding? e, Sweep? s) => e is null || s is null
                ? null
                : new EmbeddingSweepStatus(e.Profile.ProfileId, now, s.Embedded, s.SkippedPersonal, s.TooLong, s.RetryLater, s.Error);
            var shownActive = activated ? targetCoverage : activeCoverage;
            var shownTarget = activated ? null : targetCoverage;
            status.Publish(new EmbeddingStatusSnapshot(
                Idle: false,
                shownActive,
                shownTarget,
                threshold,
                (shownTarget ?? shownActive)?.Backlog ?? 0,
                Report(active, activeSweep),
                Report(target, targetSweep),
                activated ? now : status.LastActivatedAt));
        }

        var error = activeSweep?.Error ?? targetSweep?.Error ?? activeError ?? targetError;
        return new IndexRunResult(
            Idle: false,
            (activeSweep?.Embedded ?? 0) + (targetSweep?.Embedded ?? 0),
            (activeSweep?.SkippedPersonal ?? 0) + (targetSweep?.SkippedPersonal ?? 0),
            error,
            (activeSweep?.TooLong ?? 0) + (targetSweep?.TooLong ?? 0),
            (activeSweep?.RetryLater ?? 0) + (targetSweep?.RetryLater ?? 0),
            activated);
    }

    private async Task<EmbeddingCoverage> CoverageAsync(Guid profileId, Sweep sweep, CancellationToken cancellationToken)
    {
        var coverage = await store.CountCoverageAsync(profileId, personalData, cancellationToken).ConfigureAwait(false);

        // Blocks the model refused are skipped, never truncated, so they could never be covered. A sweep that did not finish found only some
        // of them, so the denominator is trusted only after a clean sweep.
        return sweep.Error is null && sweep.TooLong > 0 ? coverage with { Embeddable = Math.Max(0, coverage.Embeddable - sweep.TooLong) } : coverage;
    }

    private async Task<Sweep> SweepAsync(ActiveEmbedding active, CancellationToken cancellationToken)
    {
        var embedded = 0;
        var skipped = 0;
        var tooLong = 0;
        BlockCursor? cursor = null;
        while (true)
        {
            var page = await store.ListPendingAsync(active.Profile.ProfileId, cursor, batchSize, cancellationToken).ConfigureAwait(false);
            if (page.Count == 0)
            {
                break;
            }

            // The cursor moves past skipped blocks too, so a personal block never stalls the walk or gets re-listed.
            cursor = new BlockCursor(page[^1].DocId, page[^1].Ordinal);
            var eligible = new List<PendingBlock>(page.Count);
            foreach (var block in page)
            {
                if (personalData.IsPersonalData(block.Body))
                {
                    skipped++;
                }
                else
                {
                    eligible.Add(block);
                }
            }

            if (eligible.Count > 0)
            {
                try
                {
                    var dims = active.Profile.Dimensions;
                    var embeddedBlocks = new List<EmbeddedBlock>(eligible.Count);
                    try
                    {
                        var result = await active.Provider.EmbedAsync(eligible.Select(b => b.Body).ToArray(), cancellationToken).ConfigureAwait(false);
                        Check(result, eligible.Count, dims, active.Profile.ProfileId);
                        embeddedBlocks.AddRange(eligible.Select((b, i) => new EmbeddedBlock(b.DocId, b.Ordinal, b.TextHash, result.Vectors[i])));
                    }
                    catch (EmbeddingInputTooLongException)
                    {
                        // One block is over the model's cap. Find it by embedding the blocks one at a time and skip only that one.
                        foreach (var block in eligible)
                        {
                            try
                            {
                                var single = await active.Provider.EmbedAsync([block.Body], cancellationToken).ConfigureAwait(false);
                                Check(single, 1, dims, active.Profile.ProfileId);
                                embeddedBlocks.Add(new EmbeddedBlock(block.DocId, block.Ordinal, block.TextHash, single.Vectors[0]));
                            }
                            catch (EmbeddingInputTooLongException)
                            {
                                tooLong++;
                                if (_tooLongLogged.TryAdd((active.Profile.ProfileId, block.DocId, block.Ordinal, Convert.ToHexString(block.TextHash)), 0))
                                {
                                    LogTooLong(active.Profile.ProfileId, block.DocId, block.Ordinal);
                                }
                            }
                        }
                    }

                    if (embeddedBlocks.Count > 0)
                    {
                        embedded += await store.UpsertAsync(active.Profile.ProfileId, embeddedBlocks, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (EmbeddingException ex)
                {
                    return new Sweep(embedded, skipped, tooLong, eligible.Count, ex.Message);
                }
            }

            if (page.Count < batchSize)
            {
                break;
            }
        }

        return new Sweep(embedded, skipped, tooLong, 0, null);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Vault block {DocId}#{Ordinal} is too long for embedding profile {ProfileId} and stays lexical-only (logged once per block text)")]
    private partial void LogTooLong(Guid profileId, Guid docId, int ordinal);

    [LoggerMessage(Level = LogLevel.Information, Message = "Embedding profile {ProfileId} is covered ({Embedded} of {Embeddable} blocks) and is now the active profile")]
    private partial void LogActivated(Guid profileId, long embedded, long embeddable);

    [LoggerMessage(Level = LogLevel.Information, Message = "Embedding profile {ProfileId} backfill at {Embedded} of {Embeddable} blocks (activates at {Threshold})")]
    private partial void LogSwitchProgress(Guid profileId, long embedded, long embeddable, decimal threshold);

    private static void Check(EmbeddingResult result, int expected, int dims, Guid profileId)
    {
        if (result.Vectors.Count != expected || result.Vectors.Any(v => v.Length != dims))
        {
            throw new EmbeddingProfileMismatchException($"The provider returned vectors that do not fit profile {profileId} ({dims} dimensions).");
        }
    }
}
