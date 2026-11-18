using System.Globalization;
using Bazaar.Kodo.Runtime;

namespace Bazaar.Kodo.Sentinel;

/// <summary>The result of <see cref="SlotSwitcher.PromoteAsync"/>.</summary>
public enum PromotionOutcome
{
    /// <summary>The candidate verified, the swap happened, and the probation window passed.</summary>
    Promoted,

    /// <summary>The candidate did not verify, or could not be swapped in. The active pointer was not moved.</summary>
    Rejected,

    /// <summary>The candidate failed probation and the previous slot was re-verified and swapped back in.</summary>
    RolledBack,

    /// <summary>A rollback was needed but could not complete. The candidate stays active and the status reports failing.</summary>
    RollbackBlocked,
}

/// <summary>
/// Starts, promotes and rolls back the active runtime slot (doc 46 sections 6.3 and 7.8). The sentinel is the only writer of
/// the pointer files. It follows a pointer only after the pointer, the manifest it names and every file in the slot verify.
/// A failed check never passes silently: the status reports a failing or sealed phase with a cause.
/// </summary>
/// <remarks>
/// Start, promotion and the probation run are serialised by one gate. <see cref="RecordKodoExit"/> is not gated, so the
/// supervisor must not call it while a start or promotion is running (PROPOSED, doc 46 7.8).
/// </remarks>
public sealed class SlotSwitcher : IDisposable
{
    private readonly ISentinelFileStore _files;
    private readonly SlotPointerVerifier _pointers;
    private readonly RuntimeManifestVerifier _manifests;
    private readonly IKodoProcessLauncher _launcher;
    private readonly ISentinelJournal _journal;
    private readonly ISentinelStatusFile _statusFile;
    private readonly RestartBackoff _restarts;
    private readonly TimeProvider _clock;
    private readonly SlotSwitcherOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SentinelStatus _status = SentinelStatus.Starting;

    /// <summary>Creates a switcher over the given seams. Signatures are checked with <paramref name="signatures"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The probation window is not positive.</exception>
    public SlotSwitcher(
        ISentinelFileStore files,
        RuntimeSignatureChecker signatures,
        IKodoProcessLauncher launcher,
        ISentinelJournal journal,
        ISentinelStatusFile statusFile,
        RestartBackoff restarts,
        TimeProvider clock,
        SlotSwitcherOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(signatures);
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _statusFile = statusFile ?? throw new ArgumentNullException(nameof(statusFile));
        _restarts = restarts ?? throw new ArgumentNullException(nameof(restarts));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _options = options ?? SlotSwitcherOptions.Default;
        if (_options.ProbationWindow <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The probation window must be positive.");
        }

        _pointers = new SlotPointerVerifier(signatures);
        _manifests = new RuntimeManifestVerifier(signatures);
    }

    /// <summary>The status as last written to the status file.</summary>
    public SentinelStatus Status => _status;

    /// <summary>Releases the gate. The switcher must not be used afterwards.</summary>
    public void Dispose() => _gate.Dispose();

    /// <summary>
    /// The start check (doc 46 sections 6.3 and 7.8). Removes a stray temp pointer, then follows the active pointer if it verifies.
    /// If it does not, the verified previous pointer becomes active, which is a rollback. If neither verifies, the sentinel is sealed.
    /// </summary>
    public async Task<SentinelStatus> StartAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            RemoveStrayTemp();

            (_, SlotCheck active) = ReadAndCheck(RuntimeLayout.PointerFileName);
            Remember(active);
            if (active.IsVerified)
            {
                _status = _status with { Phase = SentinelPhases.Running, ActiveSlot = active.SlotId, Cause = null, Detail = null };
                return Publish();
            }

            (byte[]? previousBytes, SlotCheck previous) = ReadAndCheck(RuntimeLayout.PreviousPointerFileName);
            if (previousBytes is null || previous.SlotId is not { } previousSlot || !previous.IsVerified)
            {
                _status = _status with
                {
                    Phase = SentinelPhases.Sealed,
                    ActiveSlot = null,
                    Cause = active.Cause,
                    Detail = $"{Describe(active)}; previous {Describe(previous)}",
                };
                return Publish();
            }

            // Startup fallback (doc 46 6.3): the active pointer failed, so the verified previous pointer takes its place.
            // The rejected bytes are not kept, and the previous file is left as it was.
            _ = Flip(active.SlotId, fromVerified: false, active.Cause ?? SentinelCauses.PointerNotVerified, Describe(active), previousBytes, previousSlot, leftBytes: null);
            return _status;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Promotes a candidate pointer to a new slot (doc 46 sections 6.3 and 7.8). The candidate must verify, and must name a slot
    /// other than the active one. The current pointer is kept as the previous pointer, the candidate becomes active, and the slot
    /// runs its probation window. A candidate that does not survive, or whose files change during it, is rolled back.
    /// </summary>
    public async Task<PromotionOutcome> PromoteAsync(byte[] candidatePointer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidatePointer);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            SlotCheck candidate = CheckPointerBytes(candidatePointer);
            Remember(candidate);
            if (!candidate.IsVerified)
            {
                return Reject(SentinelCauses.CandidateRejected, Describe(candidate));
            }

            if (candidate.SlotId == _status.ActiveSlot)
            {
                return Reject(SentinelCauses.CandidateIsActive, $"slot {candidate.SlotId} is already active");
            }

            if (!TryReadRoot(RuntimeLayout.PointerFileName, out byte[]? currentBytes))
            {
                return Reject(SentinelCauses.PointerUnreadable, "the active pointer could not be read, so it cannot be kept as previous");
            }

            if (currentBytes is not null && !TryReplaceRoot(RuntimeLayout.PreviousPointerFileName, currentBytes))
            {
                return Reject(SentinelCauses.PointerWriteFailed, "the previous pointer could not be written");
            }

            if (!TryReplaceRoot(RuntimeLayout.PointerFileName, candidatePointer))
            {
                return Reject(SentinelCauses.PointerWriteFailed, "the active pointer could not be swapped");
            }

            _status = _status with { Phase = SentinelPhases.Probation, ActiveSlot = candidate.SlotId, Cause = null, Detail = null };
            Publish();

            KodoRunOutcome run = await _launcher.RunProbationAsync(candidate.SlotId!, _options.ProbationWindow, cancellationToken);
            if (run.Kind == KodoRunKind.SurvivedWindow)
            {
                // The window passed, but the pointer and the slot must still be the ones that were promoted.
                (byte[]? activeBytes, SlotCheck recheck) = ReadAndCheck(RuntimeLayout.PointerFileName);
                Remember(recheck);
                if (recheck.IsVerified && activeBytes is not null && activeBytes.AsSpan().SequenceEqual(candidatePointer.AsSpan()))
                {
                    _status = _status with { Phase = SentinelPhases.Running, Cause = null, Detail = null };
                    Publish();
                    return PromotionOutcome.Promoted;
                }

                return RollBackAfterProbation(candidate.SlotId!, candidatePointer, "pointer or slot changed during probation: " + Describe(recheck));
            }

            string reason = run.Kind == KodoRunKind.Exited
                ? $"exited with code {run.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "unknown"} during probation"
                : "failed to start for probation";
            return RollBackAfterProbation(candidate.SlotId!, candidatePointer, reason);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Records one exit of Kodo and publishes the restart count (doc 46 7.1). Returns the delay before the next start. See the
    /// remarks on the class for the gate.
    /// </summary>
    public RestartDecision RecordKodoExit(DateTimeOffset startedAt, DateTimeOffset exitedAt)
    {
        RestartDecision decision = _restarts.RecordExit(startedAt, exitedAt);
        Publish();
        return decision;
    }

    /// <summary>
    /// Rolls back after a failed probation. <paramref name="candidatePointer"/> is the pointer being left, so it becomes the
    /// previous pointer and a second rollback goes forward again.
    /// </summary>
    private PromotionOutcome RollBackAfterProbation(string candidateSlot, byte[] candidatePointer, string detail)
    {
        (byte[]? previousBytes, SlotCheck previous) = ReadAndCheck(RuntimeLayout.PreviousPointerFileName);
        if (previousBytes is null || previous.SlotId is not { } previousSlot || !previous.IsVerified)
        {
            return Block(candidateSlot, SentinelCauses.ProbationFailed, detail, previous);
        }

        return Flip(candidateSlot, fromVerified: true, SentinelCauses.ProbationFailed, detail, previousBytes, previousSlot, candidatePointer);
    }

    private PromotionOutcome Block(string fromSlot, string cause, string detail, SlotCheck previous)
    {
        // A missing previous slot and an unverified previous pointer are reported apart, so an operator knows which one to fix.
        string blockedCause = previous.Cause is SentinelCauses.PointerMissing or SentinelCauses.SlotMissing
            ? SentinelCauses.PreviousSlotMissing
            : SentinelCauses.PreviousNotVerified;
        _status = _status with
        {
            Phase = SentinelPhases.Failing,
            ActiveSlot = fromSlot,
            Cause = blockedCause,
            Detail = $"{cause}: {detail}; previous {Describe(previous)}",
        };
        Publish();
        return PromotionOutcome.RollbackBlocked;
    }

    private PromotionOutcome Reject(string cause, string detail)
    {
        _status = _status with { Phase = SentinelPhases.Failing, Cause = cause, Detail = detail };
        Publish();
        return PromotionOutcome.Rejected;
    }

    /// <summary>
    /// Writes the rollback journal event, swaps the active pointer to the target, and keeps the left pointer as previous. The
    /// journal comes first, so the event exists before the flip is announced (doc 46 6.3). The order of the two pointer writes
    /// matters: if the process dies between them, both pointers name the older slot, which is safe.
    /// </summary>
    private PromotionOutcome Flip(
        string? fromSlot,
        bool fromVerified,
        string cause,
        string detail,
        byte[] targetBytes,
        string targetSlot,
        byte[]? leftBytes)
    {
        DateTimeOffset now = _clock.GetUtcNow();
        if (!TryJournal(new SentinelJournalEvent(now, SentinelJournalEvent.RollbackKind, fromSlot, targetSlot, cause, detail)))
        {
            detail += "; journal write failed";
        }

        if (!TryReplaceRoot(RuntimeLayout.PointerFileName, targetBytes))
        {
            _status = _status with
            {
                Phase = SentinelPhases.Failing,
                ActiveSlot = fromVerified ? fromSlot : null,
                Cause = SentinelCauses.PointerWriteFailed,
                Detail = $"{cause}: {detail}; swap to {targetSlot} failed",
            };
            Publish();
            return PromotionOutcome.RollbackBlocked;
        }

        if (leftBytes is not null && !TryReplaceRoot(RuntimeLayout.PreviousPointerFileName, leftBytes))
        {
            detail += "; previous pointer not updated";
        }

        _status = _status with
        {
            Phase = SentinelPhases.Failing,
            ActiveSlot = targetSlot,
            Cause = cause,
            Detail = detail,
            LastRollback = new SentinelRollback(now, fromSlot, targetSlot, cause),
        };
        Publish();
        return PromotionOutcome.RolledBack;
    }

    /// <summary>Reads the pointer at a root name and checks it. A missing file is a failed check, not a null pass.</summary>
    private (byte[]? Bytes, SlotCheck Check) ReadAndCheck(string name)
    {
        if (!TryReadRoot(name, out byte[]? bytes))
        {
            return (null, SlotCheck.Failed(null, SentinelCauses.PointerUnreadable));
        }

        return bytes is null
            ? (null, SlotCheck.Failed(null, SentinelCauses.PointerMissing))
            : (bytes, CheckPointerBytes(bytes));
    }

    /// <summary>Verifies pointer bytes, the manifest they name, and every file in that slot.</summary>
    private SlotCheck CheckPointerBytes(byte[] bytes)
    {
        SlotPointer? pointer = RuntimeManifestCodec.ParsePointer(bytes);
        if (pointer is null || !RuntimeLayout.IsSlotId(pointer.SlotId))
        {
            return SlotCheck.Failed(null, SentinelCauses.PointerMalformed);
        }

        IReadOnlyDictionary<string, ReadOnlyMemory<byte>> files;
        try
        {
            files = _files.ReadSlot(pointer.SlotId);
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            return SlotCheck.Failed(pointer.SlotId, SentinelCauses.SlotUnreadable);
        }

        if (files.Count == 0)
        {
            return SlotCheck.Failed(pointer.SlotId, SentinelCauses.SlotMissing);
        }

        byte[]? envelope = files.TryGetValue(SentinelLayout.ManifestFileName, out ReadOnlyMemory<byte> manifest)
            ? manifest.ToArray()
            : null;
        DateTimeOffset now = _clock.GetUtcNow();
        SlotPointerStatus pointerStatus = _pointers.Verify(bytes, envelope, now);
        if (pointerStatus != SlotPointerStatus.Verified)
        {
            return SlotCheck.Failed(pointer.SlotId, SentinelCauses.PointerNotVerified, pointerStatus.ToString());
        }

        // The manifest is not listed in itself, so it is left out of the file set the verifier sees.
        Dictionary<string, ReadOnlyMemory<byte>> slotFiles = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, ReadOnlyMemory<byte>> file in files)
        {
            if (file.Key != SentinelLayout.ManifestFileName)
            {
                slotFiles[file.Key] = file.Value;
            }
        }

        RuntimeVerificationResult result = _manifests.Verify(envelope, slotFiles, now);
        return result.IsVerified
            ? SlotCheck.Passed(pointer.SlotId)
            : SlotCheck.Failed(pointer.SlotId, SentinelCauses.SlotNotVerified, result.Status.ToString());
    }

    private void Remember(SlotCheck check) =>
        _status = _status with
        {
            LastVerification = new SentinelVerification(check.SlotId, check.IsVerified, check.Cause, _clock.GetUtcNow()),
        };

    private SentinelStatus Publish()
    {
        _status = _status with { RestartCount = _restarts.RestartCount };
        _statusFile.Replace(SentinelStatusCodec.Encode(_status));
        return _status;
    }

    private void RemoveStrayTemp()
    {
        try
        {
            _files.DeleteRootFile(RuntimeLayout.PointerTempFileName);
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            // A stray temp pointer is never followed, so a failed delete does not stop the start check.
        }
    }

    private bool TryReadRoot(string name, out byte[]? bytes)
    {
        try
        {
            bytes = _files.ReadRootFile(name);
            return true;
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            bytes = null;
            return false;
        }
    }

    private bool TryReplaceRoot(string name, ReadOnlyMemory<byte> bytes)
    {
        try
        {
            _files.ReplaceRootFile(name, bytes);
            return true;
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            return false;
        }
    }

    private bool TryJournal(SentinelJournalEvent sentinelEvent)
    {
        try
        {
            _journal.Append(sentinelEvent);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static bool IsFileFailure(Exception ex) => ex is IOException or UnauthorizedAccessException;

    private static string Describe(SlotCheck check) =>
        check.Detail is null ? check.Cause ?? "verified" : $"{check.Cause}: {check.Detail}";
}
