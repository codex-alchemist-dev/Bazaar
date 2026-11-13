using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc.Write;
using Npgsql;

namespace Bazaar.Server.Tests.Gc.Write;

/// <summary>Database-free checks on the edit shape the writer accepts. Validation runs before any connection is opened.</summary>
public sealed class GcEditAppendValidationTests
{
    private static readonly Guid _gc = Guid.CreateVersion7();
    private static readonly Guid _author = Guid.CreateVersion7();

    // The data source is never opened: every case below fails in validation first.
    private static GcLogWriter Writer() => new(NpgsqlDataSource.Create("Host=127.0.0.1;Port=1"), notifier: null!);

    private static Bazaar.Server.Gc.Write.MessageBody Body() => new(new byte[] { 1, 2, 3 }, Guid.CreateVersion7());

    [Fact]
    public async Task An_edit_without_a_message_id_is_refused_before_any_database_work()
    {
        var request = new AppendRequest(_gc, GcEventKinds.MessageEdit, AuthorKind.Human, _author, SourceTrust.MemberHuman, "e-1")
        {
            Version = 2,
            Message = Body(),
        };

        await Assert.ThrowsAsync<ArgumentException>(() => Writer().AppendAsync(request, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_edit_at_version_one_is_refused()
    {
        var request = new AppendRequest(_gc, GcEventKinds.MessageEdit, AuthorKind.Human, _author, SourceTrust.MemberHuman, "e-2")
        {
            MessageId = Guid.CreateVersion7(),
            Version = 1,
            Message = Body(),
        };

        await Assert.ThrowsAsync<ArgumentException>(() => Writer().AppendAsync(request, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_edit_without_a_body_is_refused()
    {
        var request = new AppendRequest(_gc, GcEventKinds.MessageEdit, AuthorKind.Human, _author, SourceTrust.MemberHuman, "e-3")
        {
            MessageId = Guid.CreateVersion7(),
            Version = 2,
        };

        await Assert.ThrowsAsync<ArgumentException>(() => Writer().AppendAsync(request, TestContext.Current.CancellationToken));
    }
}
