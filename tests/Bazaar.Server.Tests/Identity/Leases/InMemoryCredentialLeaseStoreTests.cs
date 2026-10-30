using Bazaar.Server.Identity;
using Bazaar.Server.Identity.Leases;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Tests.Identity.Leases;

public sealed class InMemoryCredentialLeaseStoreTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Issue_ThenGet_ReturnsTheActiveLease()
    {
        var time = new FakeTimeProvider(Start);
        var store = new InMemoryCredentialLeaseStore(time);
        var id = new LeaseId(Guid.CreateVersion7());

        var lease = await store.IssueAsync(id, "agent-a", new CredentialId(Guid.CreateVersion7()), "github:read", Start.AddMinutes(5), null, TestContext.Current.CancellationToken);

        Assert.NotNull(lease);
        Assert.Equal(id, lease.Id);
        Assert.Equal("github:read", lease.Scope);
        Assert.Equal(lease, await store.GetAsync(id, "agent-a", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Issue_RefusesAnApprovalIdThatAlreadyBackedALease()
    {
        var store = new InMemoryCredentialLeaseStore(new FakeTimeProvider(Start));
        var approval = Guid.CreateVersion7();
        var credential = new CredentialId(Guid.CreateVersion7());

        Assert.NotNull(await store.IssueAsync(new LeaseId(Guid.CreateVersion7()), "agent-a", credential, "s:a", Start.AddMinutes(5), approval, TestContext.Current.CancellationToken));
        Assert.True(await store.IsApprovalUsedAsync(approval, TestContext.Current.CancellationToken));
        Assert.Null(await store.IssueAsync(new LeaseId(Guid.CreateVersion7()), "agent-a", credential, "s:a", Start.AddMinutes(5), approval, TestContext.Current.CancellationToken));
        Assert.Single(await store.ListActiveAsync("agent-a", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RevokeAgent_LeavesOtherAgentsLeasesActive()
    {
        var store = new InMemoryCredentialLeaseStore(new FakeTimeProvider(Start));
        var credential = new CredentialId(Guid.CreateVersion7());
        await store.IssueAsync(new LeaseId(Guid.CreateVersion7()), "agent-a", credential, "s:a", Start.AddMinutes(5), null, TestContext.Current.CancellationToken);
        var other = await store.IssueAsync(new LeaseId(Guid.CreateVersion7()), "agent-b", credential, "s:a", Start.AddMinutes(5), null, TestContext.Current.CancellationToken);

        await store.RevokeAgentAsync("agent-a", TestContext.Current.CancellationToken);

        Assert.Empty(await store.ListActiveAsync("agent-a", TestContext.Current.CancellationToken));
        Assert.Equal(other, await store.GetAsync(other!.Id, "agent-b", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GlobalRevocation_RevokesEveryLease()
    {
        var store = new InMemoryCredentialLeaseStore(new FakeTimeProvider(Start));
        var credential = new CredentialId(Guid.CreateVersion7());
        var lease = await store.IssueAsync(new LeaseId(Guid.CreateVersion7()), "agent-a", credential, "s:a", Start.AddMinutes(5), null, TestContext.Current.CancellationToken);

        await store.RevokeAsync(new GlobalRevocation(), TestContext.Current.CancellationToken);

        Assert.Null(await store.GetAsync(lease!.Id, "agent-a", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Issue_RejectsAnExpiryNotInTheFuture()
    {
        var store = new InMemoryCredentialLeaseStore(new FakeTimeProvider(Start));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            store.IssueAsync(new LeaseId(Guid.CreateVersion7()), "agent-a", new CredentialId(Guid.CreateVersion7()), "s:a", Start, null, TestContext.Current.CancellationToken));
    }
}
