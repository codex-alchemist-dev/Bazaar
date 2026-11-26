using Bazaar.Server.Vault.Git;
using Bazaar.Server.Vault.Index;

namespace Bazaar.Server.Setup.Constitution.Tests;

internal sealed class RecordingCatchUp : IVaultCatchUp
{
    public List<(Guid Vault, string Head)> Calls { get; } = [];
    public IReadOnlyList<string> Invalid { get; set; } = [];

    public Task<VaultIndexResult> CatchUpAsync(Guid vaultId, string repoPath, string headCommit, CancellationToken cancellationToken = default)
    {
        Calls.Add((vaultId, headCommit));
        return Task.FromResult(new VaultIndexResult(headCommit, VaultIndexMode.Rebuilt, 1, 0, 1, Invalid));
    }
}

public sealed class GitConstitutionVaultTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bazaar-constitution-" + Guid.NewGuid().ToString("N"));
    private readonly RecordingCatchUp _catchUp = new();
    private readonly Guid _vaultId = Guid.NewGuid();

    public void Dispose()
    {
        if (!Directory.Exists(_root)) return;
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_root, true);
    }

    private GitConstitutionVault Vault() =>
        new(new ConstitutionVaultOptions(Path.Combine(_root, "vault"), _vaultId, "Vault Writer", "writer@example.invalid"),
            new LibGit2GitProvider(), _catchUp);

    private string File_() => Path.Combine(_root, "vault", "company", "Constitution.md");

    [Fact]
    public async Task It_initialises_the_repository_commits_the_file_and_indexes_the_head()
    {
        var result = await Vault().EnsureAsync(DefaultConstitution.Markdown, TestContext.Current.CancellationToken);

        Assert.True(result.Created);
        Assert.Matches("^[0-9a-f]{40}$", result.HeadCommit);
        var git = new LibGit2GitProvider();
        var repo = Path.Combine(_root, "vault");
        Assert.Equal(result.HeadCommit, await git.HeadAsync(repo, TestContext.Current.CancellationToken));
        Assert.Contains("company/Constitution.md", await git.ListFilesAsync(repo, result.HeadCommit, TestContext.Current.CancellationToken));
        Assert.Equal((_vaultId, result.HeadCommit), Assert.Single(_catchUp.Calls));
    }

    [Fact]
    public async Task The_committed_file_is_a_parsable_constitution_document()
    {
        var result = await Vault().EnsureAsync(DefaultConstitution.Markdown, TestContext.Current.CancellationToken);

        var text = await new LibGit2GitProvider().ReadTextAsync(Path.Combine(_root, "vault"), result.HeadCommit, IConstitutionVault.Path, TestContext.Current.CancellationToken);
        var parsed = Bazaar.Server.Vault.Markdown.VaultMarkdown.Parse(text);
        Assert.Equal("constitution", parsed.FrontMatter?.Type);
    }

    [Fact]
    public async Task A_second_call_makes_no_commit_and_keeps_the_head()
    {
        var first = await Vault().EnsureAsync(DefaultConstitution.Markdown, TestContext.Current.CancellationToken);

        var second = await Vault().EnsureAsync(DefaultConstitution.Markdown, TestContext.Current.CancellationToken);

        Assert.False(second.Created);
        Assert.Equal(first.HeadCommit, second.HeadCommit);
        var commits = await new LibGit2GitProvider().ListCommitsAsync(Path.Combine(_root, "vault"), null, first.HeadCommit, TestContext.Current.CancellationToken);
        Assert.Single(commits);
        Assert.Equal(2, _catchUp.Calls.Count);
    }

    [Fact]
    public async Task An_edited_constitution_is_never_overwritten()
    {
        await Vault().EnsureAsync(DefaultConstitution.Markdown, TestContext.Current.CancellationToken);
        var edited = DefaultConstitution.Markdown + "\nOwner edit.\n";
        await File.WriteAllTextAsync(File_(), edited, TestContext.Current.CancellationToken);

        var again = await Vault().EnsureAsync(DefaultConstitution.Markdown, TestContext.Current.CancellationToken);

        Assert.False(again.Created);
        Assert.Equal(edited, await File.ReadAllTextAsync(File_(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task It_seeds_into_a_vault_that_already_has_other_commits()
    {
        var git = new LibGit2GitProvider();
        var repo = Path.Combine(_root, "vault");
        Directory.CreateDirectory(repo);
        LibGit2Sharp.Repository.Init(repo);
        await File.WriteAllTextAsync(Path.Combine(repo, "README.md"), "hello\n", TestContext.Current.CancellationToken);
        var existing = await git.CommitAsync(repo, "first", new GitIdentity("A", "a@example.invalid"), TestContext.Current.CancellationToken);

        var result = await Vault().EnsureAsync(DefaultConstitution.Markdown, TestContext.Current.CancellationToken);

        Assert.True(result.Created);
        Assert.NotEqual(existing, result.HeadCommit);
        Assert.Equal(2, (await git.ListCommitsAsync(repo, null, result.HeadCommit, TestContext.Current.CancellationToken)).Count);
    }

    [Fact]
    public async Task A_file_the_indexer_rejects_fails_with_a_plain_message()
    {
        _catchUp.Invalid = [IConstitutionVault.Path];

        var failure = await Assert.ThrowsAsync<ConstitutionVaultException>(
            () => Vault().EnsureAsync(DefaultConstitution.Markdown, TestContext.Current.CancellationToken));

        Assert.Contains("front matter", failure.Message);
        Assert.DoesNotContain(_root, failure.Message);
    }
}
