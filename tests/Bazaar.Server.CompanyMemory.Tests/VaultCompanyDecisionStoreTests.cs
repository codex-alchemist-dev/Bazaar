using Bazaar.Server.Vault.Git;
using Bazaar.Server.Vault.Writes;
using LibGit2Sharp;

namespace Bazaar.Server.CompanyMemory.Tests;

/// <summary>The Vault adapter against a real git repository: records land as Markdown and the chain reads back from the files.</summary>
public sealed class VaultCompanyDecisionStoreTests : IDisposable
{
    private static readonly GitIdentity _identity = new("Test Writer", "writer@example.invalid");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "bazaar-cm-" + Guid.NewGuid().ToString("N"));
    private readonly VaultWriter _writer;
    private readonly CompanyMemoryService _memory;

    public VaultCompanyDecisionStoreTests()
    {
        Directory.CreateDirectory(_root);
        using (var repo = new Repository(Repository.Init(_root)))
        {
            repo.Refs.UpdateTarget("HEAD", "refs/heads/main");
            repo.Config.Set("user.name", _identity.Name);
            repo.Config.Set("user.email", _identity.Email);
            repo.Config.Set("core.autocrlf", "false");
            File.WriteAllText(Path.Combine(_root, "README.md"), "vault\n");
            Commands.Stage(repo, "*");
            var signature = new Signature(_identity.Name, _identity.Email, DateTimeOffset.UtcNow);
            repo.Commit("initial", signature, signature);
        }

        var git = new LibGit2GitProvider();
        _writer = new VaultWriter(git, _root, _identity);
        _memory = new CompanyMemoryService(new VaultCompanyDecisionStore(git, _writer, _root));
    }

    public void Dispose()
    {
        _writer.Dispose();
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task A_recorded_decision_is_a_vault_file_and_its_chain_reads_back()
    {
        var first = await _memory.RecordDecisionAsync(
            new(Guid.NewGuid(), "use-polling", "Use polling", "Poll every second.", "agent-ana"), TestContext.Current.CancellationToken);
        var second = await _memory.RecordDecisionAsync(
            new(Guid.NewGuid(), "use-notify", "Use notify", "Postgres NOTIFY instead.", "agent-ben", ["0001"], "cmsg_42"), TestContext.Current.CancellationToken);

        Assert.Equal("decisions/0001-use-polling.md", first.Path);
        var text = File.ReadAllText(Path.Combine(_root, second.Path)).Replace("\r\n", "\n");
        Assert.Contains("supersedes:\n  - 0001-use-polling\n", text);
        Assert.Contains("decidedIn: cmsg_42\n", text);

        var view = await _memory.GetChainAsync("0001", TestContext.Current.CancellationToken);

        Assert.NotNull(view);
        Assert.Equal([second.Path], view.Newer);
        Assert.Equal([second.Path], view.Current);
    }

    [Fact]
    public async Task Recording_the_same_id_again_commits_nothing_new()
    {
        var request = new RecordDecisionRequest(Guid.NewGuid(), "use-polling", "Use polling", "Poll.", "agent-ana");

        var first = await _memory.RecordDecisionAsync(request, TestContext.Current.CancellationToken);
        var again = await _memory.RecordDecisionAsync(request, TestContext.Current.CancellationToken);

        Assert.True(again.AlreadyRecorded);
        Assert.Equal(first.Path, again.Path);
    }

    [Fact]
    public async Task Superseding_an_accepted_decision_flips_its_status_in_the_same_commit()
    {
        var first = await _memory.RecordDecisionAsync(
            new(Guid.NewGuid(), "use-polling", "Use polling", "Poll.", "agent-ana"), TestContext.Current.CancellationToken);
        var firstFile = Path.Combine(_root, first.Path);
        File.WriteAllText(firstFile, File.ReadAllText(firstFile).Replace("status: proposed", "status: accepted", StringComparison.Ordinal));
        using (var repo = new Repository(_root))
        {
            Commands.Stage(repo, "*");
            var signature = new Signature(_identity.Name, _identity.Email, DateTimeOffset.UtcNow);
            repo.Commit("accept 0001", signature, signature);
        }

        var second = await _memory.RecordDecisionAsync(
            new(Guid.NewGuid(), "use-notify", "Use notify", "Notify.", "agent-ben", ["0001"]), TestContext.Current.CancellationToken);

        Assert.Contains("status: superseded", File.ReadAllText(firstFile));
        Assert.Contains("status: proposed", File.ReadAllText(Path.Combine(_root, second.Path)));
        var view = await _memory.GetChainAsync("0001", TestContext.Current.CancellationToken);
        Assert.NotNull(view);
        Assert.Equal("superseded", view.Focus.Status);
        using var check = new Repository(_root);
        Assert.Equal(0, check.RetrieveStatus().Count(e => e.State != FileStatus.Ignored));
    }

    [Fact]
    public async Task Superseding_a_proposed_decision_leaves_its_status_alone()
    {
        var first = await _memory.RecordDecisionAsync(
            new(Guid.NewGuid(), "use-polling", "Use polling", "Poll.", "agent-ana"), TestContext.Current.CancellationToken);
        await _memory.RecordDecisionAsync(
            new(Guid.NewGuid(), "use-notify", "Use notify", "Notify.", "agent-ben", ["0001"]), TestContext.Current.CancellationToken);

        Assert.Contains("status: proposed", File.ReadAllText(Path.Combine(_root, first.Path)));
    }
}
