using Bazaar.Server.Setup;

namespace Bazaar.Server.Company.Tests;

/// <summary>What the two steps cover together, against the section registry: the apply is held for what no step covers, and only for that.</summary>
public sealed class CoverageTests
{
    private static IEnumerable<string> Covered() =>
        new CompanyProfileStep(new MemoryProfileStore(), new MemoryAudit()).Covers
            .Concat(new OwnerStep(new MemoryOwnerStore(), new MemoryAudit()).Covers);

    [Fact]
    public void Every_covered_section_id_is_a_registered_section()
    {
        Assert.All(Covered(), id => Assert.True(SetupPlanSections.IsKnown(id), id));
    }

    [Fact]
    public void Identity_owner_and_recovery_are_no_longer_unapplied_but_backup_owners_still_are()
    {
        var plan = Plans.Plan(bundleRef: "secret://bazaar/owner/b", printed: true);
        var withBackups = new SetupPlan
        {
            Install = plan.Install,
            Owner = new SetupOwnerSection
            {
                DisplayName = plan.Owner.DisplayName,
                Recovery = new SetupRecoverySection { BundleRef = "secret://bazaar/owner/b", PrintedCodes = true, BackupOwners = ["alice"] },
            },
        };

        var unapplied = SetupPlanSections.Unapplied(withBackups, Covered()).Select(section => section.Id).ToList();

        Assert.Equal(["owner.backupOwners"], unapplied);
    }
}
