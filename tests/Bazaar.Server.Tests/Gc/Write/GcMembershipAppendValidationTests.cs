using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc.Model;
using Bazaar.Server.Gc.Write;
using Npgsql;

namespace Bazaar.Server.Tests.Gc.Write;

/// <summary>Database-free checks on membership appends. Validation runs before any connection is opened.</summary>
public sealed class GcMembershipAppendValidationTests
{
    private static readonly Guid _gc = Guid.CreateVersion7();
    private static readonly Guid _actor = Guid.CreateVersion7();

    // The data source is never opened: the case below fails in validation first.
    private static GcLogWriter Writer() => new(NpgsqlDataSource.Create("Host=127.0.0.1;Port=1"), notifier: null!);

    [Fact]
    public async Task A_membership_event_that_also_carries_a_body_is_refused_before_any_database_work()
    {
        var request = new AppendRequest(_gc, GcEventKinds.System, AuthorKind.Human, _actor, SourceTrust.MemberHuman, "m-1")
        {
            Membership = new MembershipChange(Guid.CreateVersion7(), "member", false),
            Message = new Bazaar.Server.Gc.Write.MessageBody(new byte[] { 1 }, Guid.CreateVersion7()),
        };

        await Assert.ThrowsAsync<ArgumentException>(() => Writer().AppendAsync(request, TestContext.Current.CancellationToken));
    }
}
