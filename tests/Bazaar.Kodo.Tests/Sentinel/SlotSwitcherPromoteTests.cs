using System.Text;
using Bazaar.Kodo.Runtime;
using Bazaar.Kodo.Sentinel;
using Bazaar.Kodo.Tests.Runtime;

namespace Bazaar.Kodo.Tests.Sentinel;

public sealed class SlotSwitcherPromoteTests
{
    /// <summary>A rig with slot A active and running, and slot B installed but not yet promoted.</summary>
    private static async Task<(SentinelHarness.Rig Rig, byte[] PointerA, byte[] PointerB)> ActiveAWithCandidateB()
    {
        SentinelHarness.Rig rig = SentinelHarness.NewRig();
        byte[] pointerA = SentinelHarness.InstallSlot(rig.Store, SentinelHarness.SlotA, "a");
        byte[] pointerB = SentinelHarness.InstallSlot(rig.Store, SentinelHarness.SlotB, "b");
        rig.Store.Root[RuntimeLayout.PointerFileName] = pointerA;
        await rig.Switcher.StartAsync(CancellationToken.None);
        rig.Log.Clear();
        rig.StatusFile.Writes.Clear();
        return (rig, pointerA, pointerB);
    }

    [Fact]
    public async Task VerifiedCandidateSwapsRunsProbationAndIsKept()
    {
        (SentinelHarness.Rig rig, byte[] pointerA, byte[] pointerB) = await ActiveAWithCandidateB();

        PromotionOutcome outcome = await rig.Switcher.PromoteAsync(pointerB, CancellationToken.None);

        Assert.Equal(PromotionOutcome.Promoted, outcome);
        Assert.Equal(pointerB, rig.Store.Root[RuntimeLayout.PointerFileName]);
        Assert.Equal(pointerA, rig.Store.Root[RuntimeLayout.PreviousPointerFileName]);
        Assert.Equal(SentinelPhases.Running, rig.Switcher.Status.Phase);
        Assert.Equal(SentinelHarness.SlotB, rig.Switcher.Status.ActiveSlot);
        Assert.Null(rig.Switcher.Status.Cause);

        (string slot, TimeSpan window) = Assert.Single(rig.Launcher.Calls);
        Assert.Equal(SentinelHarness.SlotB, slot);
        Assert.Equal(SentinelHarness.Window, window);
    }

    [Fact]
    public async Task ProbationIsAnnouncedBeforeItRuns()
    {
        (SentinelHarness.Rig rig, _, byte[] pointerB) = await ActiveAWithCandidateB();

        await rig.Switcher.PromoteAsync(pointerB, CancellationToken.None);

        // The status file shows probation before the launcher's run is recorded as finished, and ends with running.
        Assert.Contains(rig.StatusFile.Writes, write => write.Contains("\"phase\":\"probation\"", StringComparison.Ordinal));
        Assert.Contains("\"phase\":\"running\"", rig.StatusFile.Writes[^1], StringComparison.Ordinal);
        Assert.Equal(["replace:kodo.pointer.previous.json", "replace:kodo.pointer.json", "status", "probation:slot-b", "status"], rig.Log);
    }

    [Fact]
    public async Task CandidateSignedByAnotherKeyIsRejectedWithoutMovingThePointer()
    {
        (SentinelHarness.Rig rig, byte[] pointerA, _) = await ActiveAWithCandidateB();
        byte[] forged = SentinelHarness.InstallSlot(rig.Store, SentinelHarness.SlotB, "b", RuntimeTestSigning.OtherKey);

        PromotionOutcome outcome = await rig.Switcher.PromoteAsync(forged, CancellationToken.None);

        Assert.Equal(PromotionOutcome.Rejected, outcome);
        Assert.Equal(pointerA, rig.Store.Root[RuntimeLayout.PointerFileName]);
        Assert.DoesNotContain(rig.Log, entry => entry.StartsWith("replace:", StringComparison.Ordinal));
        Assert.Empty(rig.Launcher.Calls);
        Assert.Equal(SentinelPhases.Failing, rig.Switcher.Status.Phase);
        Assert.Equal(SentinelCauses.CandidateRejected, rig.Switcher.Status.Cause);
        Assert.Equal(SentinelHarness.SlotA, rig.Switcher.Status.ActiveSlot);
        Assert.Contains("BadSignature", rig.Switcher.Status.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CandidateWhoseFilesChangedIsRejected()
    {
        (SentinelHarness.Rig rig, byte[] pointerA, byte[] pointerB) = await ActiveAWithCandidateB();
        rig.Store.Slots[SentinelHarness.SlotB]["kodo.dll"] = Encoding.UTF8.GetBytes("tampered");

        PromotionOutcome outcome = await rig.Switcher.PromoteAsync(pointerB, CancellationToken.None);

        Assert.Equal(PromotionOutcome.Rejected, outcome);
        Assert.Equal(pointerA, rig.Store.Root[RuntimeLayout.PointerFileName]);
        Assert.Equal(SentinelCauses.CandidateRejected, rig.Switcher.Status.Cause);
        Assert.Contains(SentinelCauses.SlotNotVerified, rig.Switcher.Status.Detail, StringComparison.Ordinal);
        Assert.False(rig.Switcher.Status.LastVerification!.Verified);
    }

    [Fact]
    public async Task CandidateNamingTheActiveSlotIsRejected()
    {
        (SentinelHarness.Rig rig, byte[] pointerA, _) = await ActiveAWithCandidateB();

        PromotionOutcome outcome = await rig.Switcher.PromoteAsync(pointerA, CancellationToken.None);

        Assert.Equal(PromotionOutcome.Rejected, outcome);
        Assert.Equal(SentinelCauses.CandidateIsActive, rig.Switcher.Status.Cause);
        Assert.Equal(SentinelHarness.SlotA, rig.Switcher.Status.ActiveSlot);
        Assert.Empty(rig.Launcher.Calls);
    }

    [Fact]
    public async Task FailedPointerSwapRejectsTheCandidate()
    {
        (SentinelHarness.Rig rig, byte[] pointerA, byte[] pointerB) = await ActiveAWithCandidateB();
        rig.Store.FailingReplaces.Add(RuntimeLayout.PointerFileName);

        PromotionOutcome outcome = await rig.Switcher.PromoteAsync(pointerB, CancellationToken.None);

        Assert.Equal(PromotionOutcome.Rejected, outcome);
        Assert.Equal(pointerA, rig.Store.Root[RuntimeLayout.PointerFileName]);
        Assert.Equal(SentinelCauses.PointerWriteFailed, rig.Switcher.Status.Cause);
        Assert.Empty(rig.Launcher.Calls);
    }

    [Fact]
    public async Task ProbationExitRollsBackToThePreviousSlotAndJournalsBeforeTheAnnouncement()
    {
        (SentinelHarness.Rig rig, byte[] pointerA, byte[] pointerB) = await ActiveAWithCandidateB();
        rig.Launcher.Outcomes.Enqueue(new KodoRunOutcome(KodoRunKind.Exited, 3));

        PromotionOutcome outcome = await rig.Switcher.PromoteAsync(pointerB, CancellationToken.None);

        Assert.Equal(PromotionOutcome.RolledBack, outcome);
        Assert.Equal(pointerA, rig.Store.Root[RuntimeLayout.PointerFileName]);
        Assert.Equal(pointerB, rig.Store.Root[RuntimeLayout.PreviousPointerFileName]);

        SentinelStatus status = rig.Switcher.Status;
        Assert.Equal(SentinelPhases.Failing, status.Phase);
        Assert.Equal(SentinelHarness.SlotA, status.ActiveSlot);
        Assert.Equal(SentinelCauses.ProbationFailed, status.Cause);
        Assert.Equal("exited with code 3 during probation", status.Detail);
        Assert.NotNull(status.LastRollback);
        Assert.Equal(SentinelHarness.SlotB, status.LastRollback.FromSlot);
        Assert.Equal(SentinelHarness.SlotA, status.LastRollback.ToSlot);

        SentinelJournalEvent journalEvent = Assert.Single(rig.Journal.Events);
        Assert.Equal(SentinelJournalEvent.RollbackKind, journalEvent.Kind);
        Assert.Equal(SentinelHarness.SlotB, journalEvent.FromSlot);
        Assert.Equal(SentinelHarness.SlotA, journalEvent.ToSlot);

        // The journal is written before the pointer flip, and the flip before the final status announcement.
        int journalAt = rig.Log.IndexOf("journal:sentinel.rollback");
        int flipAt = rig.Log.LastIndexOf("replace:kodo.pointer.json");
        int announceAt = rig.Log.LastIndexOf("status");
        Assert.True(journalAt >= 0 && journalAt < flipAt && flipAt < announceAt);
    }

    [Fact]
    public async Task FailedToStartDuringProbationRollsBack()
    {
        (SentinelHarness.Rig rig, byte[] pointerA, byte[] pointerB) = await ActiveAWithCandidateB();
        rig.Launcher.Outcomes.Enqueue(new KodoRunOutcome(KodoRunKind.FailedToStart, null));

        PromotionOutcome outcome = await rig.Switcher.PromoteAsync(pointerB, CancellationToken.None);

        Assert.Equal(PromotionOutcome.RolledBack, outcome);
        Assert.Equal(pointerA, rig.Store.Root[RuntimeLayout.PointerFileName]);
        Assert.Equal("failed to start for probation", rig.Switcher.Status.Detail);
    }

    [Fact]
    public async Task FilesChangedDuringProbationRollBack()
    {
        (SentinelHarness.Rig rig, byte[] pointerA, byte[] pointerB) = await ActiveAWithCandidateB();
        rig.Launcher.BeforeOutcome = () => rig.Store.Slots[SentinelHarness.SlotB]["kodo.dll"] = Encoding.UTF8.GetBytes("tampered");

        PromotionOutcome outcome = await rig.Switcher.PromoteAsync(pointerB, CancellationToken.None);

        Assert.Equal(PromotionOutcome.RolledBack, outcome);
        Assert.Equal(pointerA, rig.Store.Root[RuntimeLayout.PointerFileName]);
        Assert.Equal(SentinelCauses.ProbationFailed, rig.Switcher.Status.Cause);
        Assert.Contains("changed during probation", rig.Switcher.Status.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RollbackStopsWhenThePreviousSlotIsGoneAndKeepsTheCandidateFailing()
    {
        (SentinelHarness.Rig rig, _, byte[] pointerB) = await ActiveAWithCandidateB();
        rig.Store.Slots.Remove(SentinelHarness.SlotA);
        rig.Launcher.Outcomes.Enqueue(new KodoRunOutcome(KodoRunKind.Exited, 1));

        PromotionOutcome outcome = await rig.Switcher.PromoteAsync(pointerB, CancellationToken.None);

        Assert.Equal(PromotionOutcome.RollbackBlocked, outcome);
        Assert.Equal(pointerB, rig.Store.Root[RuntimeLayout.PointerFileName]);
        Assert.Equal(SentinelPhases.Failing, rig.Switcher.Status.Phase);
        Assert.Equal(SentinelHarness.SlotB, rig.Switcher.Status.ActiveSlot);
        Assert.Equal(SentinelCauses.PreviousSlotMissing, rig.Switcher.Status.Cause);
        Assert.Contains(SentinelCauses.ProbationFailed, rig.Switcher.Status.Detail, StringComparison.Ordinal);
        Assert.Null(rig.Switcher.Status.LastRollback);
        Assert.Empty(rig.Journal.Events);
    }

    [Fact]
    public async Task RollbackStopsWhenThePreviousSlotNoLongerVerifies()
    {
        (SentinelHarness.Rig rig, _, byte[] pointerB) = await ActiveAWithCandidateB();
        rig.Store.Slots[SentinelHarness.SlotA]["kodo.dll"] = Encoding.UTF8.GetBytes("tampered");
        rig.Launcher.Outcomes.Enqueue(new KodoRunOutcome(KodoRunKind.Exited, 1));

        PromotionOutcome outcome = await rig.Switcher.PromoteAsync(pointerB, CancellationToken.None);

        Assert.Equal(PromotionOutcome.RollbackBlocked, outcome);
        Assert.Equal(pointerB, rig.Store.Root[RuntimeLayout.PointerFileName]);
        Assert.Equal(SentinelCauses.PreviousNotVerified, rig.Switcher.Status.Cause);
    }

    [Fact]
    public async Task RollbackNeverDeletesTheSlotItLeaves()
    {
        (SentinelHarness.Rig rig, _, byte[] pointerB) = await ActiveAWithCandidateB();
        rig.Launcher.Outcomes.Enqueue(new KodoRunOutcome(KodoRunKind.Exited, 1));

        await rig.Switcher.PromoteAsync(pointerB, CancellationToken.None);

        Assert.True(rig.Store.Slots.ContainsKey(SentinelHarness.SlotB));
        Assert.DoesNotContain(rig.Log, entry => entry.StartsWith("delete:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task JournalFailureDuringRollbackStillFlipsAndRecordsIt()
    {
        (SentinelHarness.Rig rig, byte[] pointerA, byte[] pointerB) = await ActiveAWithCandidateB();
        rig.Journal.Fail = true;
        rig.Launcher.Outcomes.Enqueue(new KodoRunOutcome(KodoRunKind.Exited, 1));

        PromotionOutcome outcome = await rig.Switcher.PromoteAsync(pointerB, CancellationToken.None);

        Assert.Equal(PromotionOutcome.RolledBack, outcome);
        Assert.Equal(pointerA, rig.Store.Root[RuntimeLayout.PointerFileName]);
        Assert.Contains("journal write failed", rig.Switcher.Status.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecordedExitsRestartTheBackoffScheduleAndStableRunsResetIt()
    {
        (SentinelHarness.Rig rig, _, _) = await ActiveAWithCandidateB();
        DateTimeOffset now = rig.Clock.GetUtcNow();

        RestartDecision first = rig.Switcher.RecordKodoExit(now, now.AddSeconds(2));
        RestartDecision second = rig.Switcher.RecordKodoExit(now, now.AddSeconds(2));
        RestartDecision stable = rig.Switcher.RecordKodoExit(now, now.AddMinutes(30));

        Assert.Equal(TimeSpan.FromSeconds(1), first.Delay);
        Assert.Equal(TimeSpan.FromSeconds(2), second.Delay);
        Assert.Equal(TimeSpan.FromSeconds(1), stable.Delay);
        Assert.Equal(3, rig.Switcher.Status.RestartCount);
        Assert.Contains("\"restartCount\":3", rig.StatusFile.Writes[^1], StringComparison.Ordinal);
    }
}
