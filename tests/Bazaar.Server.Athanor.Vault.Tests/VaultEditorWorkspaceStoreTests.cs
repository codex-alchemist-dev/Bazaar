using Bazaar.Server.Athanor;
using Bazaar.Server.Athanor.Vault;
using Bazaar.Server.Vault.Git;
using Bazaar.Server.Vault.Writes;
using LibGit2Sharp;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Athanor.Vault.Tests;

/// <summary>A Vault workspace saves through the Vault writer: one merge commit with trailers, a clean checkout, conflicts handed back.</summary>
public sealed class VaultEditorWorkspaceStoreTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _base = Path.Combine(Path.GetTempPath(), "athanor-vault-" + Guid.NewGuid().ToString("N"));
    private readonly string _repoPath;
    private readonly LibGit2GitProvider _git = new();
    private readonly VaultWriter _writer;
    private readonly VaultEditorWorkspaceStore _store;

    public VaultEditorWorkspaceStoreTests()
    {
        _repoPath = Path.Combine(_base, "vault");
        Directory.CreateDirectory(Path.Combine(_repoPath, "notes"));
        Repository.Init(_repoPath);
        File.WriteAllText(Path.Combine(_repoPath, "notes", "a.md"), "hello\n");
        Commit("seed");

        var options = new VaultEditorOptions("vault", _repoPath, Path.Combine(_base, "worktrees"), new GitIdentity("Athanor", "athanor@example.test"));
        _writer = new VaultWriter(_git, _repoPath, new GitIdentity("Vault", "vault@example.test"));
        var limits = new TestLimits();
        var inner = new DirectoryEditorWorkspaceStore(
            new ConfiguredEditorWorkspaceResolver(new AthanorWorkspaceOptions
            {
                Workspaces = [new EditorWorkspaceEntry { Id = "vault", Kind = EditorWorkspaceKind.Vault, Path = _repoPath }],
            }),
            limits);
        _store = new VaultEditorWorkspaceStore(inner, _git, _writer, limits, options);
    }

    public void Dispose()
    {
        _writer.Dispose();
        foreach (var file in Directory.EnumerateFiles(_base, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_base, recursive: true);
    }

    private void Commit(string message)
    {
        using var repo = new Repository(_repoPath);
        Commands.Stage(repo, "*");
        var who = new Signature("t", "t@example.test", DateTimeOffset.UtcNow);
        repo.Commit(message, who, who);
    }

    private static string Rev(string text) => DirectoryEditorWorkspaceStore.RevisionOf(System.Text.Encoding.UTF8.GetBytes(text));

    private static byte[] Bytes(string text) => System.Text.Encoding.UTF8.GetBytes(text);

    private string Disk() => File.ReadAllText(Path.Combine(_repoPath, "notes", "a.md"));

    [Fact]
    public async Task A_save_lands_as_a_merge_commit_with_the_edit_trailers_and_leaves_a_clean_checkout()
    {
        var result = await _store.WriteAsync("vault", "notes/a.md", Rev("hello\n"), Bytes("hello world\n"), Ct);

        Assert.Equal(EditorWriteStatus.Written, result.Status);
        Assert.Equal(Rev("hello world\n"), result.NewRevision);
        Assert.Equal("hello world\n", Disk());
        using var repo = new Repository(_repoPath);
        Assert.Equal(2, repo.Head.Tip.Parents.Count());
        Assert.Contains("Bazaar-Agent: athanor", repo.Head.Tip.Message);
        Assert.False(repo.RetrieveStatus(new StatusOptions()).IsDirty);
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(_base, "worktrees")));
        Assert.DoesNotContain(repo.Branches, b => b.FriendlyName.StartsWith("athanor/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_stale_base_revision_returns_the_server_copy_and_commits_nothing()
    {
        var before = new Repository(_repoPath).Head.Tip.Sha;

        var result = await _store.WriteAsync("vault", "notes/a.md", Rev("older\n"), Bytes("mine\n"), Ct);

        Assert.Equal(EditorWriteStatus.Conflict, result.Status);
        Assert.Equal(Rev("hello\n"), result.ServerRevision);
        Assert.Equal("hello\n", System.Text.Encoding.UTF8.GetString(result.ServerContent!));
        Assert.Equal(before, new Repository(_repoPath).Head.Tip.Sha);
    }

    [Fact]
    public async Task Saving_unchanged_text_commits_nothing()
    {
        var before = new Repository(_repoPath).Head.Tip.Sha;

        var result = await _store.WriteAsync("vault", "notes/a.md", Rev("hello\n"), Bytes("hello\n"), Ct);

        Assert.Equal(EditorWriteStatus.Written, result.Status);
        Assert.Equal(before, new Repository(_repoPath).Head.Tip.Sha);
    }

    [Fact]
    public async Task Two_saves_in_a_row_each_land_on_the_previous_revision()
    {
        var first = await _store.WriteAsync("vault", "notes/a.md", Rev("hello\n"), Bytes("one\n"), Ct);
        var second = await _store.WriteAsync("vault", "notes/a.md", first.NewRevision!, Bytes("two\n"), Ct);

        Assert.Equal(EditorWriteStatus.Written, second.Status);
        Assert.Equal("two\n", Disk());
    }

    [Fact]
    public async Task A_missing_file_and_a_git_segment_are_refused_and_reads_pass_through()
    {
        var missing = await _store.WriteAsync("vault", "notes/none.md", "x", Bytes("x"), Ct);
        var git = await _store.WriteAsync("vault", ".git/config", "x", Bytes("x"), Ct);
        var read = await _store.ReadAsync("vault", "notes/a.md", 1000, Ct);

        Assert.Equal(EditorWriteStatus.NotFound, missing.Status);
        Assert.Equal(EditorWriteStatus.Blocked, git.Status);
        Assert.Equal(EditorReadStatus.Ok, read.Status);
        Assert.Equal(Rev("hello\n"), read.Revision);
    }

    [Fact]
    public async Task The_extension_replaces_the_resolver_and_store_for_the_vault_workspace_only()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAthanorLimits>(new TestLimits());
        services.AddBazaarAthanor();
        services.AddSingleton<IGitProvider>(_git);
        services.AddSingleton<IVaultWriter>(_writer);
        services.AddBazaarAthanorVault(new VaultEditorOptions("vault", _repoPath, Path.Combine(_base, "worktrees"), new GitIdentity("Athanor", "athanor@example.test")));
        using var sp = services.BuildServiceProvider();

        var store = sp.GetRequiredService<IEditorWorkspaceStore>();
        var info = await store.GetWorkspaceAsync("vault", Ct);
        var result = await store.WriteAsync("vault", "notes/a.md", Rev("hello\n"), Bytes("via di\n"), Ct);

        Assert.IsType<VaultEditorWorkspaceStore>(store);
        Assert.Equal(EditorWorkspaceKind.Vault, info!.Kind);
        Assert.Equal(EditorWriteStatus.Written, result.Status);
        Assert.Equal("via di\n", Disk());
        Assert.Null(await store.GetWorkspaceAsync("other", Ct));
    }

    [Fact]
    public async Task A_new_file_lands_as_a_merge_commit_and_exists_afterwards()
    {
        var created = await _store.CreateAsync("vault", "notes/b.md", Bytes("# b\n"), Ct);
        var again = await _store.CreateAsync("vault", "notes/b.md", Bytes("other\n"), Ct);
        var noFolder = await _store.CreateAsync("vault", "missing/c.md", Bytes("c\n"), Ct);
        var read = await _store.ReadAsync("vault", "notes/b.md", 1000, Ct);

        Assert.Equal(EditorCreateStatus.Created, created.Status);
        Assert.Equal(Rev("# b\n"), created.Revision);
        Assert.Equal(EditorCreateStatus.Exists, again.Status);
        Assert.Equal(EditorCreateStatus.ParentMissing, noFolder.Status);
        Assert.Equal("# b\n", File.ReadAllText(Path.Combine(_repoPath, "notes", "b.md")));
        Assert.Equal(Rev("# b\n"), read.Revision);
        using var repo = new Repository(_repoPath);
        Assert.Equal(2, repo.Head.Tip.Parents.Count());
        Assert.False(repo.RetrieveStatus(new StatusOptions()).IsDirty);
        Assert.DoesNotContain(repo.Branches, b => b.FriendlyName.StartsWith("athanor/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Deleting_a_branch_keeps_one_that_is_not_merged_and_the_checked_out_one()
    {
        using (var repo = new Repository(_repoPath))
        {
            var unmerged = repo.CreateBranch("athanor/unmerged");
            Commands.Checkout(repo, unmerged);
            File.WriteAllText(Path.Combine(_repoPath, "notes", "b.md"), "b\n");
            Commit("unmerged work");
            Commands.Checkout(repo, repo.Branches["master"] ?? repo.Branches["main"]);
            repo.CreateBranch("athanor/merged");
        }

        Assert.False(await _git.DeleteMergedBranchAsync(_repoPath, "athanor/unmerged", Ct));
        Assert.True(await _git.DeleteMergedBranchAsync(_repoPath, "athanor/merged", Ct));
        Assert.False(await _git.DeleteMergedBranchAsync(_repoPath, "athanor/missing", Ct));
        using var after = new Repository(_repoPath);
        Assert.NotNull(after.Branches["athanor/unmerged"]);
        Assert.Null(after.Branches["athanor/merged"]);
    }

    private sealed class TestLimits : IAthanorLimits
    {
        public long MaxOpenBytes => 1_000_000;
        public long MaxSaveBytes => 1_000_000;
        public int SaveRatePerMinute => 60;
    }
}
