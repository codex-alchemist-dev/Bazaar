using System.Text;
using Bazaar.Kodo.Sentinel;
using Bazaar.Kodo.Tests.Runtime;

namespace Bazaar.Kodo.Tests.Sentinel;

public sealed class SentinelStatusCodecTests
{
    [Fact]
    public void StartingStatusHasAFixedShapeWithEveryFieldWritten()
    {
        string json = Encoding.UTF8.GetString(SentinelStatusCodec.Encode(SentinelStatus.Starting));

        Assert.Equal(
            "{\"schemaVersion\":1,\"phase\":\"starting\",\"activeSlot\":null,\"cause\":null,\"detail\":null,"
            + "\"lastVerification\":null,\"restartCount\":0,\"lastRollback\":null}",
            json);
    }

    [Fact]
    public void FailingStatusWithVerificationAndRollbackHasAStableEncoding()
    {
        DateTimeOffset at = RuntimeTestSigning.Now;
        SentinelStatus status = new(
            SentinelStatus.CurrentSchemaVersion,
            SentinelPhases.Failing,
            SentinelHarness.SlotA,
            SentinelCauses.ProbationFailed,
            "exited with code 3 during probation",
            new SentinelVerification(SentinelHarness.SlotB, false, SentinelCauses.SlotNotVerified, at),
            2,
            new SentinelRollback(at, SentinelHarness.SlotB, SentinelHarness.SlotA, SentinelCauses.ProbationFailed));

        string json = Encoding.UTF8.GetString(SentinelStatusCodec.Encode(status));

        Assert.Equal(
            "{\"schemaVersion\":1,\"phase\":\"failing\",\"activeSlot\":\"slot-a\",\"cause\":\"probation_failed\","
            + "\"detail\":\"exited with code 3 during probation\","
            + "\"lastVerification\":{\"slot\":\"slot-b\",\"verified\":false,\"cause\":\"slot_not_verified\",\"checkedAt\":\"2026-11-18T12:00:00+00:00\"},"
            + "\"restartCount\":2,"
            + "\"lastRollback\":{\"at\":\"2026-11-18T12:00:00+00:00\",\"fromSlot\":\"slot-b\",\"toSlot\":\"slot-a\",\"cause\":\"probation_failed\"}}",
            json);
    }

    [Fact]
    public void EncodingIsDeterministicForTheSameStatus()
    {
        byte[] first = SentinelStatusCodec.Encode(SentinelStatus.Starting);
        byte[] second = SentinelStatusCodec.Encode(SentinelStatus.Starting);

        Assert.Equal(first, second);
    }

    [Fact]
    public void CauseCodesAreStableStrings()
    {
        // The status and the journal are read without this code, so the wire strings must not change silently.
        Assert.Equal("pointer_missing", SentinelCauses.PointerMissing);
        Assert.Equal("slot_not_verified", SentinelCauses.SlotNotVerified);
        Assert.Equal("probation_failed", SentinelCauses.ProbationFailed);
        Assert.Equal("previous_slot_missing", SentinelCauses.PreviousSlotMissing);
        Assert.Equal("sentinel.rollback", SentinelJournalEvent.RollbackKind);
        Assert.Equal("kodo.sentinel.status.json", SentinelLayout.StatusFileName);
    }
}
