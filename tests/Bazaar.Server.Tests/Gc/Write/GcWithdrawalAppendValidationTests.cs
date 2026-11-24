using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc.Write;
using Npgsql;

namespace Bazaar.Server.Tests.Gc.Write;

/// <summary>Database-free checks on the withdrawn-by-import flag (docs/design/01 Edits, PROPOSED). Validation runs before any connection is opened.</summary>
public sealed class GcWithdrawalAppendValidationTests
{
    private static GcLogWriter Writer() => new(NpgsqlDataSource.Create("Host=127.0.0.1;Port=1"), notifier: null!);

    private static AppendRequest Edit(AuthorKind kind, string? job) =>
        new(Guid.CreateVersion7(), GcEventKinds.MessageEdit, kind, Guid.Empty, SourceTrust.System, "w-1")
        {
            MessageId = Guid.CreateVersion7(),
            Version = 2,
            Message = new MessageBody(new byte[] { 1 }, Guid.CreateVersion7()),
            WithdrawnByImport = job,
        };

    [Fact]
    public async Task A_withdrawal_by_a_member_is_refused()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Writer().AppendAsync(Edit(AuthorKind.Human, "job-1"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_withdrawal_on_a_plain_message_is_refused()
    {
        var request = new AppendRequest(Guid.CreateVersion7(), GcEventKinds.Message, AuthorKind.System, Guid.Empty, SourceTrust.System, "w-2")
        {
            Message = new MessageBody(new byte[] { 1 }, Guid.CreateVersion7()),
            WithdrawnByImport = "job-1",
        };

        await Assert.ThrowsAsync<ArgumentException>(() => Writer().AppendAsync(request, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_empty_or_overlong_job_id_is_refused()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Writer().AppendAsync(Edit(AuthorKind.System, ""), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => Writer().AppendAsync(Edit(AuthorKind.System, new string('j', 201)), TestContext.Current.CancellationToken));
    }
}
