using Bazaar.Server.Tests.Vault.Data;
using Bazaar.Server.Tests.Vault.Git;
using Bazaar.Server.Vault.Data;
using Bazaar.Server.Vault.Git;
using Bazaar.Server.Vault.Index;
using Bazaar.Server.Vault.Links;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Tests.Vault.Index;

/// <summary>Indexer behaviour against real Postgres and real git history (doc 18, Indexing).</summary>
public sealed class VaultIndexerTests(VaultDb db) : IClassFixture<VaultDb>
{
    private const string _alphaId = "01JABCDEFGHJKMNPQRSTVWXYZ0";
    private const string _betaId = "01JABCDEFGHJKMNPQRSTVWXYZ1";
    private const string _gammaId = "01JABCDEFGHJKMNPQRSTVWXYZ2";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    private static string Doc(string id, string title, string body) =>
        $"---\nid: {id}\ntype: research\ntitle: {title}\n---\n{body}";

    private VaultIndexer Indexer() => new(
        new LibGit2GitProvider(),
        new VaultIndexRepository(db.DataSource),
        new VaultStateRepository(db.DataSource),
        new VaultCommitRepository(db.DataSource),
        VaultAmbiguousLinkPolicy.Warn);

    private VaultIndexRepository Store() => new(db.DataSource);

    [Fact]
    public async Task Incremental_catch_up_matches_a_full_rebuild_over_a_scripted_history()
    {
        SkipWithoutDatabase();
        using var repo = new TempGitRepository(
            ("company/alpha.md", Doc(_alphaId, "Alpha", "# Intro\n\nSee [[Beta]] and [[Beta#Part]].\n\nA paragraph ^para1\n")),
            ("company/beta.md", Doc(_betaId, "Beta", "## Part\n\nLinks [[Alpha#Intro]] and [[Gamma]].\n")));
        var vault = Guid.NewGuid();
        var rebuilt = Guid.NewGuid();
        using var indexer = Indexer();

        var first = await indexer.CatchUpAsync(vault, repo.Checkout, "main", Ct);
        Assert.Equal(VaultIndexMode.Rebuilt, first.Mode);
        var alphaId = (await Store().GetDocumentByPathAsync(vault, "company/alpha.md", Ct))!.DocId;

        // Step 2: move alpha into an archive folder (same content), add gamma, and make beta link to it.
        Directory.CreateDirectory(Path.Combine(repo.Checkout, "archive"));
        File.Move(Path.Combine(repo.Checkout, "company", "alpha.md"), Path.Combine(repo.Checkout, "archive", "alpha.md"));
        TempGitRepository.WriteFile(repo.Checkout, "company/gamma.md", Doc(_gammaId, "Gamma", "Back to [[Beta]].\n"));
        TempGitRepository.WriteFile(repo.Checkout, "company/beta.md", Doc(_betaId, "Beta", "## Part\n\nLinks [[Alpha#Intro]] and [[Gamma]].\n"));
        TempGitRepository.CommitAll(repo.Checkout, "step 2");

        var second = await indexer.CatchUpAsync(vault, repo.Checkout, "main", Ct);
        Assert.Equal(VaultIndexMode.Incremental, second.Mode);
        Assert.Equal(alphaId, (await Store().GetDocumentByPathAsync(vault, "archive/alpha.md", Ct))!.DocId);

        // Step 3: delete gamma; beta's link to it becomes unresolved again.
        File.Delete(Path.Combine(repo.Checkout, "company", "gamma.md"));
        TempGitRepository.CommitAll(repo.Checkout, "step 3");
        var third = await indexer.CatchUpAsync(vault, repo.Checkout, "main", Ct);
        Assert.Equal(VaultIndexMode.Incremental, third.Mode);
        Assert.Null(await Store().GetDocumentByPathAsync(vault, "company/gamma.md", Ct));

        var again = await indexer.CatchUpAsync(vault, repo.Checkout, "main", Ct);
        Assert.Equal(VaultIndexMode.UpToDate, again.Mode);

        // The index must match what a full rebuild of the same head produces.
        var fromScratch = await indexer.CatchUpAsync(rebuilt, repo.Checkout, "main", Ct);
        Assert.Equal(VaultIndexMode.Rebuilt, fromScratch.Mode);
        Assert.Equal(await SnapshotAsync(rebuilt), await SnapshotAsync(vault));

        // vault_commit mirrors the git log: initial, step 2 and step 3.
        Assert.Equal(3L, await CountCommitsAsync(vault));
        Assert.Equal(3L, await CountCommitsAsync(rebuilt));
    }

    [Fact]
    public async Task Links_into_a_document_resolve_when_it_appears_and_fall_back_when_it_goes()
    {
        SkipWithoutDatabase();
        using var repo = new TempGitRepository(("one.md", Doc(_alphaId, "One", "Points at [[Two]].\n")));
        var vault = Guid.NewGuid();
        using var indexer = Indexer();
        await indexer.CatchUpAsync(vault, repo.Checkout, "main", Ct);
        var one = (await Store().GetDocumentByPathAsync(vault, "one.md", Ct))!.DocId;
        Assert.Null((await Store().GetOutgoingLinksAsync(one, Ct)).Single().TargetDoc);

        TempGitRepository.WriteFile(repo.Checkout, "two.md", Doc(_betaId, "Two", "Here.\n"));
        TempGitRepository.CommitAll(repo.Checkout, "add two");
        await indexer.CatchUpAsync(vault, repo.Checkout, "main", Ct);
        var two = (await Store().GetDocumentByPathAsync(vault, "two.md", Ct))!.DocId;
        Assert.Equal(two, (await Store().GetOutgoingLinksAsync(one, Ct)).Single().TargetDoc);
    }

    [Fact]
    public async Task A_missing_indexed_commit_forces_a_rebuild()
    {
        SkipWithoutDatabase();
        using var repo = new TempGitRepository(("one.md", Doc(_alphaId, "One", "Text.\n")));
        var vault = Guid.NewGuid();
        using var indexer = Indexer();
        await new VaultStateRepository(db.DataSource).TryAdvanceAsync(vault, null, new string('a', 40), Ct);

        var result = await indexer.CatchUpAsync(vault, repo.Checkout, "main", Ct);

        Assert.Equal(VaultIndexMode.Rebuilt, result.Mode);
        Assert.NotNull(await Store().GetDocumentByPathAsync(vault, "one.md", Ct));
    }

    private async Task<List<string>> SnapshotAsync(Guid vaultId)
    {
        var rows = new List<string>();
        rows.AddRange(await QueryAsync(vaultId, """
            SELECT 'doc|' || path || '|' || title || '|' || type || '|' || area || '|' || coalesce(project, '') || '|'
                   || coalesce(owner, '') || '|' || frontmatter::text
            FROM vault_docs WHERE vault_id = $1
            """));
        rows.AddRange(await QueryAsync(vaultId, """
            SELECT 'block|' || d.path || '|' || b.ordinal || '|' || coalesce(b.block_id, '') || '|'
                   || array_to_string(b.heading_path, '/') || '|' || b.start_line || '|' || b.end_line || '|' || encode(b.text_hash, 'hex')
            FROM vault_blocks b JOIN vault_docs d ON d.doc_id = b.doc_id WHERE d.vault_id = $1
            """));
        rows.AddRange(await QueryAsync(vaultId, """
            SELECT 'link|' || s.path || '|' || l.src_ordinal || '|' || l.link_ord || '|' || l.kind || '|' || l.target_raw || '|'
                   || coalesce(t.path, '-') || '|' || coalesce(l.target_anchor, '') || '|' || coalesce(l.alias, '')
            FROM vault_links l
                 JOIN vault_docs s ON s.doc_id = l.src_doc
                 LEFT JOIN vault_docs t ON t.doc_id = l.target_doc
            WHERE s.vault_id = $1
            """));
        rows.Sort(StringComparer.Ordinal);
        return rows;
    }

    private async Task<List<string>> QueryAsync(Guid vaultId, string sql)
    {
        await using var cmd = db.DataSource.CreateCommand(sql);
        cmd.Parameters.AddWithValue(vaultId);
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        var rows = new List<string>();
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }

    private async Task<long> CountCommitsAsync(Guid vaultId)
    {
        await using var cmd = db.DataSource.CreateCommand("SELECT count(*) FROM vault_commit WHERE vault_id = $1");
        cmd.Parameters.AddWithValue(vaultId);
        return (long)(await cmd.ExecuteScalarAsync(Ct))!;
    }
}
