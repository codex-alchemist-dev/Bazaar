using Bazaar.Community.Drafts;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Community.Tests.Drafts;

/// <summary>Runs the draft store against a real PostgreSQL with the server's own <c>community_draft</c> migration (0005).</summary>
public sealed class NpgsqlCommunityDraftStoreTests
{
    private static async Task<PgTestDatabase> CreateDbAsync(CancellationToken ct)
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        return await PgTestDatabase.CreateAsync("community_draft", MigrateAsync, ct);
    }

    // Applies migration community/0005 as shipped. The role grants are dropped: the test role setup is not part of this check.
    private static async Task MigrateAsync(NpgsqlDataSource dataSource, CancellationToken ct)
    {
        var assembly = typeof(Bazaar.Server.Data.Migrations.MigrationRunner).Assembly;
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith("0005_community_draft.sql", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        var sql = await new StreamReader(stream).ReadToEndAsync(ct);
        var kept = string.Join('\n', sql.Split('\n').Where(l => !l.StartsWith("REVOKE", StringComparison.Ordinal) && !l.StartsWith("GRANT", StringComparison.Ordinal)));
        await using var cmd = dataSource.CreateCommand(kept);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static CommunityDraft NewDraft(DraftFixture f) => f.NewDraft(gatesPass: false);

    [Fact]
    public async Task Round_trip_keeps_every_field()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await CreateDbAsync(ct);
        using var f = new DraftFixture();
        var store = new NpgsqlCommunityDraftStore(db.DataSource);
        var draft = NewDraft(f) with { TaintSourcesJson = "[\"web\"]" };
        draft = draft with { Post = draft.Post with { HeadBranch = "feat", BaseBranch = "main" } };

        Assert.True(await store.TryAddAsync(draft, ct));
        var read = await store.GetAsync(draft.DraftId, ct);

        Assert.NotNull(read);
        Assert.Equal(draft.Post, read.Post);
        Assert.Equal(draft.GateInput.Body, read.GateInput.Body);
        Assert.Equal(draft.GateInput.DuplicateSearchQueries, read.GateInput.DuplicateSearchQueries);
        Assert.Equal(draft.Profile, read.Profile);
        Assert.Equal(draft.ApprovalHash, read.ApprovalHash);
        Assert.Equal(draft.ApprovalId, read.ApprovalId);
        Assert.Equal(draft.ApprovalExpiresAt, read.ApprovalExpiresAt);
        Assert.Equal(draft.GateFailures, read.GateFailures);
        Assert.NotEmpty(read.GateFailures);
        Assert.Equal(CommunityDraftStatus.Pending, read.Status);
        Assert.Equal("[\"web\"]", read.TaintSourcesJson.Replace(" ", string.Empty, StringComparison.Ordinal));
        Assert.Equal(draft.Action, read.Action);
        Assert.Null(await store.GetAsync(Guid.NewGuid(), ct));
    }

    [Fact]
    public async Task Adding_the_same_id_twice_stores_one_row()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await CreateDbAsync(ct);
        using var f = new DraftFixture();
        var store = new NpgsqlCommunityDraftStore(db.DataSource);
        var draft = NewDraft(f);

        Assert.True(await store.TryAddAsync(draft, ct));
        Assert.False(await store.TryAddAsync(draft with { SponsorId = "other" }, ct));

        Assert.Equal("sponsor-1", (await store.GetAsync(draft.DraftId, ct))!.SponsorId);
    }

    [Fact]
    public async Task Status_changes_are_compare_and_set_through_to_posted()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await CreateDbAsync(ct);
        using var f = new DraftFixture();
        var store = new NpgsqlCommunityDraftStore(db.DataSource);
        var draft = NewDraft(f);
        await store.TryAddAsync(draft, ct);

        Assert.True(await store.TryReplaceAsync(draft.DraftId, CommunityDraftStatus.Pending, draft with { Status = CommunityDraftStatus.Posting }, ct));
        Assert.False(await store.TryReplaceAsync(draft.DraftId, CommunityDraftStatus.Pending, draft with { Status = CommunityDraftStatus.Denied }, ct));
        var posted = draft with { Status = CommunityDraftStatus.Posted, ExternalId = "ext-1", Url = "https://example.test/i/1", Reused = true };
        Assert.True(await store.TryReplaceAsync(draft.DraftId, CommunityDraftStatus.Posting, posted, ct));

        var read = (await store.GetAsync(draft.DraftId, ct))!;
        Assert.Equal(CommunityDraftStatus.Posted, read.Status);
        Assert.Equal("ext-1", read.ExternalId);
        Assert.Equal("https://example.test/i/1", read.Url);
        Assert.True(read.Reused);
    }

    [Fact]
    public async Task Posting_can_fall_back_to_pending_or_fail_and_pending_can_be_denied_or_expire()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await CreateDbAsync(ct);
        using var f = new DraftFixture();
        var store = new NpgsqlCommunityDraftStore(db.DataSource);
        var a = NewDraft(f);
        var b = NewDraft(f);
        var c = NewDraft(f);
        foreach (var d in new[] { a, b, c }) await store.TryAddAsync(d, ct);

        await store.TryReplaceAsync(a.DraftId, CommunityDraftStatus.Pending, a with { Status = CommunityDraftStatus.Posting }, ct);
        Assert.True(await store.TryReplaceAsync(a.DraftId, CommunityDraftStatus.Posting, a, ct));
        Assert.Equal(CommunityDraftStatus.Pending, (await store.GetAsync(a.DraftId, ct))!.Status);
        await store.TryReplaceAsync(a.DraftId, CommunityDraftStatus.Pending, a with { Status = CommunityDraftStatus.Posting }, ct);
        Assert.True(await store.TryReplaceAsync(a.DraftId, CommunityDraftStatus.Posting, a with { Status = CommunityDraftStatus.Failed }, ct));
        Assert.True(await store.TryReplaceAsync(b.DraftId, CommunityDraftStatus.Pending, b with { Status = CommunityDraftStatus.Denied }, ct));
        Assert.True(await store.TryReplaceAsync(c.DraftId, CommunityDraftStatus.Pending, c with { Status = CommunityDraftStatus.Expired }, ct));
    }

    [Fact]
    public async Task ExpireDue_moves_only_due_pending_drafts_and_returns_their_ids()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await CreateDbAsync(ct);
        using var f = new DraftFixture();
        var store = new NpgsqlCommunityDraftStore(db.DataSource);
        var due = NewDraft(f) with { ApprovalExpiresAt = DraftFixture.Start };
        var late = NewDraft(f) with { ApprovalExpiresAt = DraftFixture.Start.AddSeconds(1) };
        var decided = NewDraft(f) with { ApprovalExpiresAt = DraftFixture.Start.AddHours(-1) };
        foreach (var d in new[] { due, late, decided }) await store.TryAddAsync(d, ct);
        await store.TryReplaceAsync(decided.DraftId, CommunityDraftStatus.Pending, decided with { Status = CommunityDraftStatus.Denied }, ct);

        var ids = await store.ExpireDueAsync(DraftFixture.Start, ct);

        Assert.Equal([due.DraftId], ids);
        Assert.Equal(CommunityDraftStatus.Expired, (await store.GetAsync(due.DraftId, ct))!.Status);
        Assert.Equal(CommunityDraftStatus.Pending, (await store.GetAsync(late.DraftId, ct))!.Status);
        Assert.Equal(CommunityDraftStatus.Denied, (await store.GetAsync(decided.DraftId, ct))!.Status);
        Assert.Empty(await store.ExpireDueAsync(DraftFixture.Start, ct));
        Assert.Equal(due.Post.Body, (await store.GetAsync(due.DraftId, ct))!.Post.Body);
    }

    [Fact]
    public async Task ExpireDue_run_twice_at_once_returns_each_draft_to_one_caller()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await CreateDbAsync(ct);
        using var f = new DraftFixture();
        var store = new NpgsqlCommunityDraftStore(db.DataSource);
        for (var i = 0; i < 5; i++) await store.TryAddAsync(NewDraft(f) with { ApprovalExpiresAt = DraftFixture.Start }, ct);

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => store.ExpireDueAsync(DraftFixture.Start, ct)));

        var all = results.SelectMany(r => r).ToList();
        Assert.Equal(5, all.Count);
        Assert.Equal(5, all.Distinct().Count());
    }

    [Fact]
    public async Task A_transition_the_lifecycle_forbids_throws_and_changes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await CreateDbAsync(ct);
        using var f = new DraftFixture();
        var store = new NpgsqlCommunityDraftStore(db.DataSource);
        var draft = NewDraft(f);
        await store.TryAddAsync(draft, ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.TryReplaceAsync(draft.DraftId, CommunityDraftStatus.Pending, draft with { Status = CommunityDraftStatus.Posted }, ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.TryReplaceAsync(draft.DraftId, CommunityDraftStatus.Pending, draft with { Status = CommunityDraftStatus.Edited }, ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.TryReplaceAsync(draft.DraftId, CommunityDraftStatus.Posted, draft, ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.TryReplaceAsync(Guid.NewGuid(), CommunityDraftStatus.Pending, draft with { Status = CommunityDraftStatus.Posting }, ct));
        Assert.Equal(CommunityDraftStatus.Pending, (await store.GetAsync(draft.DraftId, ct))!.Status);
    }

    [Fact]
    public async Task Two_concurrent_approvals_cannot_both_move_the_draft_to_posting()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await CreateDbAsync(ct);
        using var f = new DraftFixture();
        var store = new NpgsqlCommunityDraftStore(db.DataSource);
        var draft = NewDraft(f);
        await store.TryAddAsync(draft, ct);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            store.TryReplaceAsync(draft.DraftId, CommunityDraftStatus.Pending, draft with { Status = CommunityDraftStatus.Posting }, ct)));

        Assert.Single(results, r => r);
    }

    [Fact]
    public async Task Supersede_marks_the_original_edited_and_adds_the_replacement_in_one_transaction()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await CreateDbAsync(ct);
        using var f = new DraftFixture();
        var store = new NpgsqlCommunityDraftStore(db.DataSource);
        var original = NewDraft(f);
        var replacement = NewDraft(f);
        await store.TryAddAsync(original, ct);

        Assert.True(await store.TrySupersedeAsync(original.DraftId, replacement, ct));

        var o = (await store.GetAsync(original.DraftId, ct))!;
        Assert.Equal(CommunityDraftStatus.Edited, o.Status);
        Assert.Equal(replacement.DraftId, o.SupersededBy);
        Assert.Equal(CommunityDraftStatus.Pending, (await store.GetAsync(replacement.DraftId, ct))!.Status);
        // The original is no longer pending, so a second supersede writes nothing.
        var again = NewDraft(f);
        Assert.False(await store.TrySupersedeAsync(original.DraftId, again, ct));
        Assert.Null(await store.GetAsync(again.DraftId, ct));
    }

    [Fact]
    public async Task Supersede_rolls_back_when_the_replacement_cannot_be_inserted()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await CreateDbAsync(ct);
        using var f = new DraftFixture();
        var store = new NpgsqlCommunityDraftStore(db.DataSource);
        var original = NewDraft(f);
        var taken = NewDraft(f);
        await store.TryAddAsync(original, ct);
        await store.TryAddAsync(taken, ct);

        Assert.False(await store.TrySupersedeAsync(original.DraftId, taken with { }, ct));

        var o = (await store.GetAsync(original.DraftId, ct))!;
        Assert.Equal(CommunityDraftStatus.Pending, o.Status);
        Assert.Null(o.SupersededBy);
    }

    [Fact]
    public async Task Concurrent_supersedes_let_exactly_one_win()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await CreateDbAsync(ct);
        using var f = new DraftFixture();
        var store = new NpgsqlCommunityDraftStore(db.DataSource);
        var original = NewDraft(f);
        await store.TryAddAsync(original, ct);
        var replacements = Enumerable.Range(0, 6).Select(_ => NewDraft(f)).ToList();

        var results = await Task.WhenAll(replacements.Select(r => store.TrySupersedeAsync(original.DraftId, r, ct)));

        Assert.Single(results, r => r);
        var winner = replacements[Array.IndexOf(results, true)];
        Assert.Equal(winner.DraftId, (await store.GetAsync(original.DraftId, ct))!.SupersededBy);
        foreach (var loser in replacements.Where(r => r != winner)) Assert.Null(await store.GetAsync(loser.DraftId, ct));
    }

    [Fact]
    public async Task List_pending_returns_open_pending_drafts_newest_first_up_to_the_limit()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await CreateDbAsync(ct);
        using var f = new DraftFixture();
        var store = new NpgsqlCommunityDraftStore(db.DataSource);
        var first = NewDraft(f);
        var second = NewDraft(f);
        var third = NewDraft(f);
        var expired = NewDraft(f) with { ApprovalExpiresAt = DraftFixture.Start.AddMinutes(-5) };
        var denied = NewDraft(f);
        foreach (var d in new[] { first, second, third, expired, denied })
        {
            await store.TryAddAsync(d, ct);
            await Task.Delay(5, ct);
        }

        await store.TryReplaceAsync(denied.DraftId, CommunityDraftStatus.Pending, denied with { Status = CommunityDraftStatus.Denied }, ct);

        var all = await store.ListPendingAsync(DraftFixture.Start, 10, ct);
        var two = await store.ListPendingAsync(DraftFixture.Start, 2, ct);

        Assert.Equal([third.DraftId, second.DraftId, first.DraftId], all.Select(d => d.DraftId));
        Assert.Equal([third.DraftId, second.DraftId], two.Select(d => d.DraftId));
    }

    [Fact]
    public void Migration_0005_grants_no_delete_and_keeps_the_status_check()
    {
        var assembly = typeof(Bazaar.Server.Data.Migrations.MigrationRunner).Assembly;
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith("0005_community_draft.sql", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        var text = new StreamReader(stream).ReadToEnd();

        Assert.Contains("GRANT SELECT, INSERT, UPDATE ON community_draft TO bazaar_app;", text, StringComparison.Ordinal);
        Assert.DoesNotContain("DELETE", text.Replace("-- ", string.Empty, StringComparison.Ordinal).Split("CREATE TABLE")[1], StringComparison.Ordinal);
        Assert.Contains("'Pending', 'Posting', 'Posted', 'Denied', 'Edited', 'Failed', 'Expired'", text, StringComparison.Ordinal);
    }
}
