using System.Text;
using Bazaar.Kodo.Runtime;
using Bazaar.Kodo.Sentinel;
using Bazaar.Kodo.Tests.Runtime;

namespace Bazaar.Kodo.Tests.Sentinel;

public sealed class SlotSwitcherStartTests
{
    [Fact]
    public async Task VerifiedActivePointerRunsWithoutTouchingAnything()
    {
        SentinelHarness.Rig rig = SentinelHarness.NewRig();
        rig.Store.Root[RuntimeLayout.PointerFileName] = SentinelHarness.InstallSlot(rig.Store, SentinelHarness.SlotA, "a");
        rig.Store.Root[RuntimeLayout.PreviousPointerFileName] = SentinelHarness.InstallSlot(rig.Store, SentinelHarness.SlotB, "b");

        SentinelStatus status = await rig.Switcher.StartAsync(CancellationToken.None);

        Assert.Equal(SentinelPhases.Running, status.Phase);
        Assert.Equal(SentinelHarness.SlotA, status.ActiveSlot);
        Assert.Null(status.Cause);
        Assert.Null(status.LastRollback);
        Assert.NotNull(status.LastVerification);
        Assert.True(status.LastVerification.Verified);
        Assert.Equal(SentinelHarness.SlotA, status.LastVerification.Slot);
        Assert.DoesNotContain(rig.Log, entry => entry.StartsWith("replace:", StringComparison.Ordinal));
        Assert.Empty(rig.Journal.Events);
    }

    [Fact]
    public async Task StartWritesTheStatusFileWithTheSameStatusItReturns()
    {
        SentinelHarness.Rig rig = SentinelHarness.NewRig();
        rig.Store.Root[RuntimeLayout.PointerFileName] = SentinelHarness.InstallSlot(rig.Store, SentinelHarness.SlotA, "a");

        SentinelStatus status = await rig.Switcher.StartAsync(CancellationToken.None);

        string written = Assert.Single(rig.StatusFile.Writes);
        Assert.Equal(Encoding.UTF8.GetString(SentinelStatusCodec.Encode(status)), written);
    }

    [Fact]
    public async Task MissingActivePointerFallsBackToVerifiedPreviousAndJournalsFirst()
    {
        SentinelHarness.Rig rig = SentinelHarness.NewRig();
        byte[] previous = SentinelHarness.InstallSlot(rig.Store, SentinelHarness.SlotB, "b");
        rig.Store.Root[RuntimeLayout.PreviousPointerFileName] = previous;

        SentinelStatus status = await rig.Switcher.StartAsync(CancellationToken.None);

        Assert.Equal(SentinelPhases.Failing, status.Phase);
        Assert.Equal(SentinelHarness.SlotB, status.ActiveSlot);
        Assert.Equal(SentinelCauses.PointerMissing, status.Cause);
        Assert.NotNull(status.LastRollback);
        Assert.Null(status.LastRollback.FromSlot);
        Assert.Equal(SentinelHarness.SlotB, status.LastRollback.ToSlot);
        Assert.Equal(previous, rig.Store.Root[RuntimeLayout.PointerFileName]);
        Assert.Equal(previous, rig.Store.Root[RuntimeLayout.PreviousPointerFileName]);

        SentinelJournalEvent journalEvent = Assert.Single(rig.Journal.Events);
        Assert.Equal(SentinelJournalEvent.RollbackKind, journalEvent.Kind);
        Assert.Equal(SentinelHarness.SlotB, journalEvent.ToSlot);

        // The journal event comes before the pointer swap, and the status is the last thing announced.
        Assert.Equal(["journal:sentinel.rollback", "replace:kodo.pointer.json", "status"], ShortLog(rig.Log, "journal:", "replace:kodo.pointer.json", "status"));
    }

    [Fact]
    public async Task ActiveSlotWhoseFileChangedFallsBackAndReportsTheSlotFailure()
    {
        SentinelHarness.Rig rig = SentinelHarness.NewRig();
        rig.Store.Root[RuntimeLayout.PointerFileName] = SentinelHarness.InstallSlot(rig.Store, SentinelHarness.SlotA, "a");
        rig.Store.Root[RuntimeLayout.PreviousPointerFileName] = SentinelHarness.InstallSlot(rig.Store, SentinelHarness.SlotB, "b");
        rig.Store.Slots[SentinelHarness.SlotA]["kodo.dll"] = Encoding.UTF8.GetBytes("tampered");

        SentinelStatus status = await rig.Switcher.StartAsync(CancellationToken.None);

        Assert.Equal(SentinelPhases.Failing, status.Phase);
        Assert.Equal(SentinelHarness.SlotB, status.ActiveSlot);
        Assert.Equal(SentinelCauses.SlotNotVerified, status.Cause);
        Assert.Equal("slot_not_verified: FileMismatch", status.Detail);
        Assert.NotNull(status.LastVerification);
        Assert.False(status.LastVerification.Verified);
        Assert.Equal(SentinelHarness.SlotA, status.LastVerification.Slot);
        Assert.Equal(SentinelCauses.SlotNotVerified, status.LastRollback?.Cause);
    }

    [Fact]
    public async Task UnreadableActivePointerFallsBackAndReportsUnreadable()
    {
        SentinelHarness.Rig rig = SentinelHarness.NewRig();
        rig.Store.Root[RuntimeLayout.PointerFileName] = SentinelHarness.InstallSlot(rig.Store, SentinelHarness.SlotA, "a");
        rig.Store.Root[RuntimeLayout.PreviousPointerFileName] = SentinelHarness.InstallSlot(rig.Store, SentinelHarness.SlotB, "b");
        rig.Store.UnreadableRoots.Add(RuntimeLayout.PointerFileName);

        SentinelStatus status = await rig.Switcher.StartAsync(CancellationToken.None);

        Assert.Equal(SentinelPhases.Failing, status.Phase);
        Assert.Equal(SentinelHarness.SlotB, status.ActiveSlot);
        Assert.Equal(SentinelCauses.PointerUnreadable, status.Cause);
    }

    [Fact]
    public async Task ActivePointerWithBadSignatureFallsBackAndReportsNotVerified()
    {
        SentinelHarness.Rig rig = SentinelHarness.NewRig();
        rig.Store.Root[RuntimeLayout.PointerFileName] = SentinelHarness.InstallSlot(rig.Store, SentinelHarness.SlotA, "a", RuntimeTestSigning.OtherKey);
        rig.Store.Root[RuntimeLayout.PreviousPointerFileName] = SentinelHarness.InstallSlot(rig.Store, SentinelHarness.SlotB, "b");

        SentinelStatus status = await rig.Switcher.StartAsync(CancellationToken.None);

        Assert.Equal(SentinelHarness.SlotB, status.ActiveSlot);
        Assert.Equal(SentinelCauses.PointerNotVerified, status.Cause);
        Assert.Equal("pointer_not_verified: BadSignature", status.Detail);
    }

    [Fact]
    public async Task NoVerifiedPointerSealsTheSentinel()
    {
        SentinelHarness.Rig rig = SentinelHarness.NewRig();

        SentinelStatus status = await rig.Switcher.StartAsync(CancellationToken.None);

        Assert.Equal(SentinelPhases.Sealed, status.Phase);
        Assert.Null(status.ActiveSlot);
        Assert.Equal(SentinelCauses.PointerMissing, status.Cause);
        Assert.Equal("pointer_missing; previous pointer_missing", status.Detail);
        Assert.Null(status.LastRollback);
        Assert.DoesNotContain(rig.Log, entry => entry.StartsWith("replace:", StringComparison.Ordinal));
        Assert.Empty(rig.Journal.Events);
    }

    [Fact]
    public async Task PreviousSlotThatIsGoneSealsWhenTheActiveSlotFailed()
    {
        SentinelHarness.Rig rig = SentinelHarness.NewRig();
        rig.Store.Root[RuntimeLayout.PointerFileName] = SentinelHarness.InstallSlot(rig.Store, SentinelHarness.SlotA, "a");
        rig.Store.Root[RuntimeLayout.PreviousPointerFileName] = SentinelHarness.InstallSlot(rig.Store, SentinelHarness.SlotB, "b");
        rig.Store.Slots.Remove(SentinelHarness.SlotB);
        rig.Store.Slots[SentinelHarness.SlotA]["kodo.dll"] = Encoding.UTF8.GetBytes("tampered");

        SentinelStatus status = await rig.Switcher.StartAsync(CancellationToken.None);

        Assert.Equal(SentinelPhases.Sealed, status.Phase);
        Assert.Null(status.ActiveSlot);
        Assert.Equal(SentinelCauses.SlotNotVerified, status.Cause);
        Assert.Contains("previous slot_missing", status.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreviousPointerSignedByAnotherKeyKeepsTheSentinelSealed()
    {
        SentinelHarness.Rig rig = SentinelHarness.NewRig();
        rig.Store.Root[RuntimeLayout.PreviousPointerFileName] = SentinelHarness.InstallSlot(rig.Store, SentinelHarness.SlotB, "b", RuntimeTestSigning.OtherKey);

        SentinelStatus status = await rig.Switcher.StartAsync(CancellationToken.None);

        Assert.Equal(SentinelPhases.Sealed, status.Phase);
        Assert.Contains("previous pointer_not_verified: BadSignature", status.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StrayTempPointerIsRemovedAtStart()
    {
        SentinelHarness.Rig rig = SentinelHarness.NewRig();
        rig.Store.Root[RuntimeLayout.PointerFileName] = SentinelHarness.InstallSlot(rig.Store, SentinelHarness.SlotA, "a");
        rig.Store.Root[RuntimeLayout.PointerTempFileName] = Encoding.UTF8.GetBytes("half written");

        await rig.Switcher.StartAsync(CancellationToken.None);

        Assert.False(rig.Store.Root.ContainsKey(RuntimeLayout.PointerTempFileName));
    }

    [Fact]
    public async Task FailedFallbackSwapReportsWriteFailureAfterJournaling()
    {
        SentinelHarness.Rig rig = SentinelHarness.NewRig();
        rig.Store.Root[RuntimeLayout.PreviousPointerFileName] = SentinelHarness.InstallSlot(rig.Store, SentinelHarness.SlotB, "b");
        rig.Store.FailingReplaces.Add(RuntimeLayout.PointerFileName);

        SentinelStatus status = await rig.Switcher.StartAsync(CancellationToken.None);

        Assert.Equal(SentinelPhases.Failing, status.Phase);
        Assert.Equal(SentinelCauses.PointerWriteFailed, status.Cause);
        Assert.Null(status.ActiveSlot);
        Assert.Single(rig.Journal.Events);
        Assert.False(rig.Store.Root.ContainsKey(RuntimeLayout.PointerFileName));
    }

    [Fact]
    public async Task JournalFailureDoesNotStopTheRollbackButIsRecorded()
    {
        SentinelHarness.Rig rig = SentinelHarness.NewRig();
        byte[] previous = SentinelHarness.InstallSlot(rig.Store, SentinelHarness.SlotB, "b");
        rig.Store.Root[RuntimeLayout.PreviousPointerFileName] = previous;
        rig.Journal.Fail = true;

        SentinelStatus status = await rig.Switcher.StartAsync(CancellationToken.None);

        Assert.Equal(previous, rig.Store.Root[RuntimeLayout.PointerFileName]);
        Assert.Equal(SentinelHarness.SlotB, status.ActiveSlot);
        Assert.Contains("journal write failed", status.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StatusReportsRestartCountFromTheBackoff()
    {
        SentinelHarness.Rig rig = SentinelHarness.NewRig();
        rig.Store.Root[RuntimeLayout.PointerFileName] = SentinelHarness.InstallSlot(rig.Store, SentinelHarness.SlotA, "a");
        DateTimeOffset start = rig.Clock.GetUtcNow();

        RestartDecision decision = rig.Switcher.RecordKodoExit(start, start.AddSeconds(3));
        await rig.Switcher.StartAsync(CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(1), decision.Delay);
        Assert.Equal(1, rig.Switcher.Status.RestartCount);
    }

    /// <summary>The log entries that match the given prefixes or names, in order, so an ordering assertion ignores unrelated calls.</summary>
    private static List<string> ShortLog(IEnumerable<string> log, params string[] keep) =>
        log.Where(entry => keep.Any(k => entry == k || (k.EndsWith(':') && entry.StartsWith(k, StringComparison.Ordinal)))).ToList();
}
