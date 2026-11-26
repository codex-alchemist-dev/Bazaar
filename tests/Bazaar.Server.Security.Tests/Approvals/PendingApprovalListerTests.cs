using System.Collections.Immutable;
using Bazaar.Server.Security.Approvals;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Security.Tests.Approvals;

public sealed class PendingApprovalListerTests
{
    private static readonly DateTimeOffset _now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    private static ApprovalRequest Row(ApprovalStatus status, TimeSpan expiresIn, TimeSpan age) =>
        new(Guid.NewGuid(), "charles", Guid.Empty, "pr.merge", null, new byte[32], "{}", ImmutableArray<Bazaar.Server.Security.Taint.TaintSource>.Empty,
            status, _now - age, _now + expiresIn);

    [Fact]
    public async Task Lists_only_unexpired_pending_rows_newest_first_up_to_the_limit()
    {
        var store = new InMemoryApprovalStore();
        var old = Row(ApprovalStatus.Pending, TimeSpan.FromHours(1), TimeSpan.FromMinutes(30));
        var recent = Row(ApprovalStatus.Pending, TimeSpan.FromHours(1), TimeSpan.FromMinutes(1));
        foreach (var row in new[] { old, recent, Row(ApprovalStatus.Approved, TimeSpan.FromHours(1), TimeSpan.Zero), Row(ApprovalStatus.Pending, TimeSpan.FromSeconds(-1), TimeSpan.Zero) })
        {
            await store.InsertAsync(row, TestContext.Current.CancellationToken);
        }

        var all = await store.ListPendingAsync(_now, 10, TestContext.Current.CancellationToken);
        var one = await store.ListPendingAsync(_now, 1, TestContext.Current.CancellationToken);

        Assert.Equal([recent.Id, old.Id], all.Select(r => r.Id));
        Assert.Equal([recent.Id], one.Select(r => r.Id));
    }

    [Fact]
    public void The_owner_approvals_registration_exposes_the_lister_over_the_registered_store()
    {
        var services = new ServiceCollection();
        var store = new InMemoryApprovalStore();
        services.AddSingleton<IApprovalStore>(store);
        services.AddBazaarOwnerApprovals();
        using var provider = services.BuildServiceProvider();

        Assert.Same(store, provider.GetRequiredService<IPendingApprovalLister>());
    }
}
