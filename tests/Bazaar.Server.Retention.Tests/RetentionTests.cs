using Bazaar.Server.Retention;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Retention.Tests;

public sealed class RetentionTests
{
    private static readonly DateTimeOffset _created = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static RetentionPlanner Planner(params RetentionCategory[] rows) => new(new RetentionPolicyTable(rows));

    private static RetentionCategory Mail { get; } = new("mail", TimeSpan.FromDays(100), TimeSpan.FromDays(10), ErasureMethod.SoftDeleteThenPurge);

    [Fact]
    public void A_record_inside_its_period_is_kept()
        => Assert.Equal(RetentionDisposition.Keep, Planner(Mail).Decide("mail", _created, null, _created.AddDays(99), false));

    [Fact]
    public void An_expired_record_is_soft_deleted_first()
        => Assert.Equal(RetentionDisposition.SoftDeleteDue, Planner(Mail).Decide("mail", _created, null, _created.AddDays(100), false));

    [Fact]
    public void A_soft_deleted_record_is_kept_until_the_window_ends_then_purged()
    {
        var planner = Planner(Mail);
        var deleted = _created.AddDays(100);
        Assert.Equal(RetentionDisposition.Keep, planner.Decide("mail", _created, deleted, deleted.AddDays(9), false));
        Assert.Equal(RetentionDisposition.PurgeDue, planner.Decide("mail", _created, deleted, deleted.AddDays(10), false));
    }

    [Fact]
    public void A_legal_hold_blocks_removal_at_every_stage()
    {
        var planner = Planner(Mail);
        var deleted = _created.AddDays(100);
        Assert.Equal(RetentionDisposition.Held, planner.Decide("mail", _created, null, _created.AddDays(500), true));
        Assert.Equal(RetentionDisposition.Held, planner.Decide("mail", _created, deleted, deleted.AddDays(500), true));
    }

    [Fact]
    public void Unknown_categories_and_keep_rows_are_never_removed()
    {
        var planner = Planner(new RetentionCategory("gc_log", null, TimeSpan.Zero, ErasureMethod.None));
        Assert.Equal(RetentionDisposition.Keep, planner.Decide("gc_log", _created, null, _created.AddYears(50), false));
        Assert.Equal(RetentionDisposition.Keep, planner.Decide("nothing", _created, null, _created.AddYears(50), false));
    }

    [Fact]
    public void Crypto_erase_categories_are_left_to_the_erasure_coordinator()
    {
        var planner = Planner(new RetentionCategory("audit_read", TimeSpan.FromDays(1), TimeSpan.Zero, ErasureMethod.CryptoErase));
        Assert.Equal(RetentionDisposition.Keep, planner.Decide("audit_read", _created, null, _created.AddDays(30), false));
    }

    [Fact]
    public void The_policy_table_refuses_bad_rows()
    {
        Assert.Throws<ArgumentException>(() => new RetentionPolicyTable([new("gc_log", TimeSpan.FromDays(1), TimeSpan.Zero, ErasureMethod.SoftDeleteThenPurge)]));
        Assert.Throws<ArgumentException>(() => new RetentionPolicyTable([new("x", TimeSpan.Zero, TimeSpan.Zero, ErasureMethod.None)]));
        Assert.Throws<ArgumentException>(() => new RetentionPolicyTable([new("x", null, TimeSpan.FromDays(-1), ErasureMethod.None)]));
        Assert.Throws<ArgumentException>(() => new RetentionPolicyTable([Mail, Mail]));
    }

    private sealed class Contributor(params DataInventoryEntry[] entries) : IDataInventoryContributor
    {
        public IReadOnlyList<DataInventoryEntry> Entries { get; } = entries;
    }

    [Fact]
    public void The_inventory_merges_and_orders_entries()
    {
        var policy = new RetentionPolicyTable([Mail]);
        var inventory = new DataInventory(
            [new Contributor(new DataInventoryEntry("mail", "mail_message", ["body"], "mail")), new Contributor(new DataInventoryEntry("mail", "mail_attachment", [], "mail"))],
            policy);
        Assert.Equal(["mail_attachment", "mail_message"], inventory.Entries.Select(e => e.Store));
    }

    [Fact]
    public void The_inventory_refuses_personal_data_without_a_retention_row_or_a_duplicate_store()
    {
        var policy = new RetentionPolicyTable([Mail]);
        Assert.Throws<InvalidOperationException>(() => new DataInventory([new Contributor(new DataInventoryEntry("orphan", "t", [], "p"))], policy));
        Assert.Throws<InvalidOperationException>(() => new DataInventory(
            [new Contributor(new DataInventoryEntry("mail", "t", [], "p"), new DataInventoryEntry("mail", "t", [], "p"))], policy));
        _ = new DataInventory([new Contributor(new DataInventoryEntry("orphan", "t", [], "reviewed", ContainsPersonalData: false))], policy);
    }

    [Fact]
    public void The_component_reads_the_table_from_configuration_and_defaults_to_keep()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Bazaar:Retention:mail:PeriodDays"] = "100",
            ["Bazaar:Retention:mail:SoftDeleteDays"] = "10",
            ["Bazaar:Retention:mail:Method"] = "SoftDeleteThenPurge",
            ["Bazaar:Retention:gc_log:Method"] = "CryptoErase",
        }).Build();
        var services = new ServiceCollection();
        new RetentionComponent().AddServices(services, configuration);
        using var provider = services.BuildServiceProvider();

        var policy = provider.GetRequiredService<IRetentionPolicy>();
        Assert.Equal(TimeSpan.FromDays(100), policy.Find("mail")!.Period);
        Assert.Null(policy.Find("gc_log")!.Period);
        Assert.Empty(provider.GetRequiredService<IDataInventory>().Entries);
        Assert.Equal(RetentionDisposition.SoftDeleteDue,
            provider.GetRequiredService<IRetentionPlanner>().Decide("mail", _created, null, _created.AddDays(101), false));
    }
}
