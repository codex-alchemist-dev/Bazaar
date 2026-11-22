using Bazaar.Server.AccountApproval;
using Bazaar.Server.AccountApproval.OwnerQueue;
using Bazaar.Server.Host.Composition;
using Bazaar.Server.Security;
using Bazaar.Server.Security.Approvals;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Host.Tests;

public sealed class AccountApprovalCompositionTests
{
    [Fact]
    public void The_host_ships_both_account_approval_components_in_dependency_order()
    {
        var ids = HostComponents.All.Select(c => c.Manifest.Id).ToList();
        Assert.Contains("account-approval", ids);
        Assert.Contains("account-approval-owner-queue", ids);
        Assert.Contains("account-approval", HostComponents.All.Single(c => c is OwnerQueueComponent).Manifest.DependsOn);
        Assert.Contains(HostComponents.All, c => c is AccountApprovalComponent);
    }

    [Fact]
    public void Without_a_data_source_the_approval_store_and_enqueuer_cannot_resolve()
    {
        using var sp = new ServiceCollection().AddBazaarOwnerApprovals().BuildServiceProvider();
        Assert.Throws<InvalidOperationException>(() => sp.GetRequiredService<IApprovalStore>());
        Assert.Throws<InvalidOperationException>(() => sp.GetRequiredService<IOwnerApprovalEnqueuer>());
    }
}
