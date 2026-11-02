using Bazaar.Server.Memory.Trust;
using Bazaar.Server.Security.Quarantine;

namespace Bazaar.Server.Tests.Security.Quarantine;

public class QuarantineTierMappingTests
{
    [Theory]
    [InlineData(QuarantineSubjectKind.GcMessage)]
    [InlineData(QuarantineSubjectKind.MemoryNote)]
    [InlineData(QuarantineSubjectKind.VaultFile)]
    [InlineData(QuarantineSubjectKind.Agent)]
    [InlineData(QuarantineSubjectKind.Handover)]
    public async Task A_quarantined_subject_of_any_kind_maps_to_T0(QuarantineSubjectKind kind)
    {
        var service = new QuarantineService(new InMemoryQuarantineStore(), new RecordingAgentTokenRevoker(), new RecordingVmPauser());
        var record = await service.QuarantineAsync(new QuarantineSubject(kind, "subject"), QuarantineReason.OwnerAction, new QuarantineActor("owner-1", QuarantineActorKind.Owner), TestContext.Current.CancellationToken);

        var tier = QuarantineTierMapping.MemoryTierFor(record.Status, MemoryTier.OwnerPromoted);

        Assert.Equal(MemoryTier.Quarantined, tier);
    }

    [Theory]
    [InlineData(MemoryTier.UntrustedDerived, MemoryTier.UntrustedDerived)]
    [InlineData(MemoryTier.AgentVerified, MemoryTier.UntrustedDerived)]
    [InlineData(MemoryTier.OwnerPromoted, MemoryTier.UntrustedDerived)]
    public void A_released_subject_is_capped_at_T1_because_its_lineage_is_kept(MemoryTier unquarantined, MemoryTier expected)
    {
        Assert.Equal(expected, QuarantineTierMapping.MemoryTierFor(QuarantineStatus.Released, unquarantined));
    }

    [Theory]
    [InlineData(MemoryTier.Quarantined)]
    [InlineData(MemoryTier.UntrustedDerived)]
    [InlineData(MemoryTier.AgentVerified)]
    [InlineData(MemoryTier.OwnerPromoted)]
    public void A_subject_with_no_record_keeps_the_tier_its_write_earned(MemoryTier unquarantined)
    {
        Assert.Equal(unquarantined, QuarantineTierMapping.MemoryTierFor(null, unquarantined));
    }
}
