using Bazaar.Server.Tests.Vault.Git;
using Bazaar.Server.Vault.Git;
using Bazaar.Server.Vault.Writes;
using LibGit2Sharp;

namespace Bazaar.Server.Tests.Vault.Writes;

/// <summary>Page writes (doc 18, "Page writes"): fixed path under an allowed prefix, idempotent by id, trailers, indexer hook.</summary>
public sealed class VaultPageWriterTests : IDisposable
{
    private static readonly Guid _id = Guid.Parse("0196a7c4-0000-7000-8000-0000000000a1");

    private readonly TempGitRepository _repo = new(("README.md", "vault\n"));
    private readonly RecordingObserver _observer = new();
    private readonly VaultWriter _writer;

    public VaultPageWriterTests()
    {
        _writer = new VaultWriter(new LibGit2GitProvider(), _repo.Checkout, TempGitRepository.Writer, _observer);
    }

    public void Dispose()
    {
        _writer.Dispose();
        _repo.Dispose();
    }

    private sealed class RecordingObserver : IVaultCommitObserver
    {
        public List<(string Old, string New)> Calls { get; } = [];

        public Task OnCommitAsync(string oldCommit, string newCommit, CancellationToken ct)
        {
            Calls.Add((oldCommit, newCommit));
            return Task.CompletedTask;
        }
    }

    private static VaultPageDraft Draft(string path = "company/memory/onboarding.md", string body = "First body.", Guid? id = null) =>
        new(id ?? _id, path, "Onboarding", "memory", body, "agent-ana", "gc-3");

    [Fact]
    public async Task A_page_is_created_with_front_matter_trailers_and_an_indexer_call()
    {
        Assert.IsAssignableFrom<IVaultPageWriter>(_writer);

        var result = await _writer.CreateOrUpdatePageAsync(Draft(), TestContext.Current.CancellationToken);

        Assert.Equal(VaultPageOutcome.Created, result.Outcome);
        var text = TempGitRepository.ReadFile(_repo.Checkout, "company/memory/onboarding.md");
        Assert.StartsWith("---\nid: ", text);
        Assert.Contains("type: memory\n", text);
        Assert.Contains("title: Onboarding\n", text);
        Assert.EndsWith("First body.\n", text);
        using var repo = new Repository(_repo.Checkout);
        var message = repo.Lookup<Commit>(result.NewCommit).Message;
        Assert.Contains("Bazaar-Agent: agent-ana", message);
        Assert.Contains("Bazaar-GC: gc-3", message);
        Assert.Equal([(result.OldCommit, result.NewCommit)], _observer.Calls);
        Assert.Equal(0, TempGitRepository.DirtyEntryCount(_repo.Checkout));
    }

    [Fact]
    public async Task The_same_page_again_commits_nothing_and_changed_content_updates_it()
    {
        var first = await _writer.CreateOrUpdatePageAsync(Draft(), TestContext.Current.CancellationToken);
        var again = await _writer.CreateOrUpdatePageAsync(Draft(), TestContext.Current.CancellationToken);
        var changed = await _writer.CreateOrUpdatePageAsync(Draft(body: "Second body."), TestContext.Current.CancellationToken);

        Assert.Equal(VaultPageOutcome.UpToDate, again.Outcome);
        Assert.Equal(first.NewCommit, again.NewCommit);
        Assert.Equal(VaultPageOutcome.Updated, changed.Outcome);
        Assert.EndsWith("Second body.\n", TempGitRepository.ReadFile(_repo.Checkout, "company/memory/onboarding.md"));
        Assert.Equal(2, _observer.Calls.Count);
    }

    [Theory]
    [InlineData("notes/outside.md")]
    [InlineData("decisions/0001-x.md")]
    [InlineData("company/memory/../../escape.md")]
    [InlineData("company/memory/no-extension")]
    [InlineData("/company/memory/abs.md")]
    public async Task Paths_outside_the_allowed_prefix_or_the_vault_are_refused(string path)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _writer.CreateOrUpdatePageAsync(Draft(path), TestContext.Current.CancellationToken));
        Assert.Equal(0, TempGitRepository.DirtyEntryCount(_repo.Checkout));
    }

    [Fact]
    public async Task A_decision_type_or_an_unknown_type_is_refused()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _writer.CreateOrUpdatePageAsync(
            Draft() with { Type = "decision" }, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => _writer.CreateOrUpdatePageAsync(
            Draft() with { Type = "nonsense" }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_path_owned_by_another_id_and_an_id_at_another_path_are_refused()
    {
        await _writer.CreateOrUpdatePageAsync(Draft(), TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _writer.CreateOrUpdatePageAsync(
            Draft(id: Guid.Parse("0196a7c4-0000-7000-8000-0000000000a2")), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _writer.CreateOrUpdatePageAsync(
            Draft("company/memory/other.md"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_operator_prefix_replaces_the_default_and_decisions_stay_forbidden()
    {
        using var custom = new VaultWriter(new LibGit2GitProvider(), _repo.Checkout, TempGitRepository.Writer)
        {
            PageWritePrefixes = ["notes/", "decisions/"],
        };

        Assert.Equal(VaultPageOutcome.Created, (await custom.CreateOrUpdatePageAsync(Draft("notes/a.md"), TestContext.Current.CancellationToken)).Outcome);
        await Assert.ThrowsAsync<ArgumentException>(() => custom.CreateOrUpdatePageAsync(
            Draft("company/memory/a.md", id: Guid.NewGuid()), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => custom.CreateOrUpdatePageAsync(
            Draft("decisions/0001-x.md", id: Guid.NewGuid()), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_page_over_the_size_limit_is_refused()
    {
        using var small = new VaultWriter(new LibGit2GitProvider(), _repo.Checkout, TempGitRepository.Writer) { MaxPageBytes = 100 };

        await Assert.ThrowsAsync<ArgumentException>(() => small.CreateOrUpdatePageAsync(
            Draft(body: new string('x', 500)), TestContext.Current.CancellationToken));
    }
}
