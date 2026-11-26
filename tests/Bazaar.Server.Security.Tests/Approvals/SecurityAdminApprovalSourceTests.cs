using System.Collections.Immutable;
using Bazaar.Server.Security.Approvals;
using Bazaar.Server.Security.Taint;
using Bazaar.Server.WebAdmin;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Security.Tests.Approvals;

/// <summary>The web admin approvals section over the owner queue (doc 51 section 5): what is waiting, nothing else.</summary>
public sealed class SecurityAdminApprovalSourceTests
{
    private static readonly DateTimeOffset _now = new(2026, 11, 22, 10, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ApprovalRequest Row(string tool, string agent = "agent-a", string? target = null, ApprovalStatus status = ApprovalStatus.Pending,
        TimeSpan? age = null, TimeSpan? lifetime = null) => new(
        Guid.CreateVersion7(),
        agent,
        Guid.Empty,
        tool,
        target,
        new byte[32],
        "{\"secret\":\"payload-canary\"}",
        ImmutableArray<TaintSource>.Empty,
        status,
        _now - (age ?? TimeSpan.FromMinutes(5)),
        _now + (lifetime ?? TimeSpan.FromHours(1)));

    private static (SecurityAdminApprovalSource Source, InMemoryApprovalStore Store) Make()
    {
        var store = new InMemoryApprovalStore();
        return (new SecurityAdminApprovalSource(store, new FakeTimeProvider(_now)), store);
    }

    [Fact]
    public async Task Lists_pending_unexpired_requests_oldest_first()
    {
        var (source, store) = Make();
        var newer = Row("account.request", age: TimeSpan.FromMinutes(1));
        var older = Row("github.merge", agent: "agent-b", target: "owner/repo#12", age: TimeSpan.FromMinutes(30));
        await store.InsertAsync(newer, Ct);
        await store.InsertAsync(older, Ct);

        var rows = await source.ListPendingAsync(Ct);

        Assert.Equal(2, rows.Count);
        Assert.Equal(new AdminApproval(older.Id.ToString("D"), "github.merge owner/repo#12", "agent-b", older.RequestedAt), rows[0]);
        Assert.Equal(newer.Id.ToString("D"), rows[1].Id);
        Assert.Equal("account.request", rows[1].Action);
    }

    [Theory]
    [InlineData(ApprovalStatus.Approved)]
    [InlineData(ApprovalStatus.Denied)]
    [InlineData(ApprovalStatus.Expired)]
    [InlineData(ApprovalStatus.Consumed)]
    public async Task Decided_requests_are_not_listed(ApprovalStatus status)
    {
        var (source, store) = Make();
        await store.InsertAsync(Row("t", status: status), Ct);

        Assert.Empty(await source.ListPendingAsync(Ct));
    }

    [Fact]
    public async Task A_request_past_its_expiry_is_not_listed_even_before_it_is_marked_expired()
    {
        var (source, store) = Make();
        await store.InsertAsync(Row("t", lifetime: TimeSpan.FromSeconds(-1)), Ct);

        Assert.Empty(await source.ListPendingAsync(Ct));
    }

    [Fact]
    public async Task Shows_no_payload_and_strips_control_characters_from_agent_text()
    {
        var (source, store) = Make();
        await store.InsertAsync(Row("tool\u001b[31m\nx", agent: "agent\ra"), Ct);

        var row = Assert.Single(await source.ListPendingAsync(Ct));

        Assert.DoesNotContain("payload-canary", System.Text.Json.JsonSerializer.Serialize(row));
        Assert.DoesNotContain(row.Action, c => char.IsControl(c));
        Assert.DoesNotContain(row.Requester, c => char.IsControl(c));
    }

    [Fact]
    public async Task Long_text_is_capped()
    {
        var (source, store) = Make();
        await store.InsertAsync(Row(new string('a', 5000)), Ct);

        Assert.Equal(200, Assert.Single(await source.ListPendingAsync(Ct)).Action.Length);
    }

    [Fact]
    public async Task Registration_resolves_the_port_over_a_store_that_can_list()
    {
        var services = new ServiceCollection();
        var store = new InMemoryApprovalStore();
        services.AddSingleton<IApprovalStore>(store);
        services.AddSingleton<TimeProvider>(new FakeTimeProvider(_now));
        services.AddBazaarAdminApprovalSource();
        await store.InsertAsync(Row("t"), Ct);
        await using var provider = services.BuildServiceProvider();

        var port = provider.GetRequiredService<IAdminApprovalSource>();

        Assert.Single(await port.ListPendingAsync(Ct));
    }

    [Fact]
    public void Registration_keeps_a_port_the_host_already_registered()
    {
        var services = new ServiceCollection();
        var mine = new SecurityAdminApprovalSource(new InMemoryApprovalStore(), TimeProvider.System);
        services.AddSingleton<IAdminApprovalSource>(mine);
        services.AddBazaarAdminApprovalSource();
        using var provider = services.BuildServiceProvider();

        Assert.Same(mine, provider.GetRequiredService<IAdminApprovalSource>());
    }
}
