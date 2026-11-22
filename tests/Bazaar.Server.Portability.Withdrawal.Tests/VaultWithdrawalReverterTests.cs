using Bazaar.Server.Vault.Git;
using Bazaar.Server.Vault.Writes;

namespace Bazaar.Server.Portability.Withdrawal.Tests;

public sealed class VaultWithdrawalReverterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bazaar-withdraw-" + Guid.NewGuid().ToString("N"));
    private readonly FakeGit _git = new();
    private readonly FakeVaultWriter _writer;
    private readonly VaultWithdrawalReverter _reverter;

    public VaultWithdrawalReverterTests()
    {
        Directory.CreateDirectory(_root);
        _writer = new FakeVaultWriter(_git);
        _reverter = new VaultWithdrawalReverter(_git, _writer, FakeGit.Repo, _root, new GitIdentity("Bazaar", "bazaar@example.invalid"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static string Note(string job, string key, string body) => $"---\nid: X\ntype: draft\ntitle: {key}\nsource_trust: external\nimported_job: {job}\nsource_key: {key}\n---\n{body}";

    private static string ImportMessage(string job, int batch) => $"import {job} batch {batch}\n\nBazaar-Import-Job: {job}\nBazaar-Import-Batch: {batch}";

    private void SeedImport(string job = "job")
    {
        _git.CommitMain("seed", new() { ["README.md"] = "hello", ["notes/a.md"] = "---\nid: A\n---\noriginal" });
        _git.CommitMain(ImportMessage(job, 0), new()
        {
            ["imports/" + job + "/k1.md"] = Note(job, "k1", "one"),
            ["imports/" + job + "/k2.md"] = Note(job, "k2", "two"),
        });
    }

    [Fact]
    public async Task Deleting_files_the_import_created_lands_one_revert_commit_through_the_vault_writer()
    {
        SeedImport();
        var before = _git.Order.Count;

        await _reverter.RevertAsync("job", 0, ["k1", "k2"], CancellationToken.None);

        var request = Assert.Single(_writer.Requests);
        Assert.Equal("agent/import-withdraw-job-0", request.Branch);
        Assert.Equal(VaultWithdrawalReverter.DefaultAgentId, request.AgentId);
        Assert.Equal(Assert.Single(_git.Order.Skip(before - 1).Take(1)), request.BaseCommit);
        Assert.Equal(["README.md", "notes/a.md"], _git.Head.Tree.Keys.Order(StringComparer.Ordinal));
        Assert.Contains("Bazaar-Import-Withdraw-Job: job", _git.Head.Message);
        Assert.Equal(before + 1, _git.Order.Count); // history stays: the import commit is not rewritten
        Assert.False(Directory.Exists(Path.Combine(_root, "import-withdraw-job-0")));
    }

    [Fact]
    public async Task Restores_the_prior_text_of_a_note_a_replace_overwrote_in_place()
    {
        _git.CommitMain("seed", new() { ["notes/a.md"] = "---\nid: A\n---\noriginal" });
        _git.CommitMain(ImportMessage("job", 0), new() { ["notes/a.md"] = Note("job", "k9", "replaced") });

        await _reverter.RevertAsync("job", 0, ["k9"], CancellationToken.None);

        Assert.Equal("---\nid: A\n---\noriginal", _git.Head.Tree["notes/a.md"]);
    }

    [Fact]
    public async Task A_repeat_after_success_is_a_no_op()
    {
        SeedImport();
        await _reverter.RevertAsync("job", 0, ["k1", "k2"], CancellationToken.None);
        var head = _git.MainHead;

        await _reverter.RevertAsync("job", 0, ["k1", "k2"], CancellationToken.None);

        Assert.Equal(head, _git.MainHead);
        Assert.Single(_writer.Requests);
    }

    [Fact]
    public async Task Resumes_after_the_writer_failed_and_does_not_commit_twice()
    {
        SeedImport();
        _writer.FailTimes = 1;

        await Assert.ThrowsAsync<IOException>(() => _reverter.RevertAsync("job", 0, ["k1"], CancellationToken.None));
        Assert.True(_git.BranchExists("agent/import-withdraw-job-0"));
        var commitsAfterFailure = _git.Order.Count;

        await _reverter.RevertAsync("job", 0, ["k1"], CancellationToken.None);

        Assert.Equal(commitsAfterFailure, _git.Order.Count);
        Assert.DoesNotContain("imports/job/k1.md", _git.Head.Tree.Keys);
        Assert.Contains("imports/job/k2.md", _git.Head.Tree.Keys);
    }

    [Theory]
    [InlineData(VaultEditOutcome.Conflicted)]
    [InlineData(VaultEditOutcome.Refused)]
    public async Task A_conflict_or_refusal_fails_the_batch(VaultEditOutcome outcome)
    {
        SeedImport();
        _writer.ForcedOutcome = outcome;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _reverter.RevertAsync("job", 0, ["k1"], CancellationToken.None));

        Assert.Contains(outcome.ToString(), ex.Message);
    }

    [Fact]
    public async Task Other_jobs_files_are_left_alone()
    {
        SeedImport("job");
        _git.CommitMain(ImportMessage("other", 0), new() { ["imports/other/k1.md"] = Note("other", "k1", "x") });

        await _reverter.RevertAsync("job", 0, ["k1"], CancellationToken.None);

        Assert.Contains("imports/other/k1.md", _git.Head.Tree.Keys);
        Assert.DoesNotContain("imports/job/k1.md", _git.Head.Tree.Keys);
    }

    [Fact]
    public async Task An_unsafe_job_id_is_refused()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _reverter.RevertAsync("../x", 0, ["k1"], CancellationToken.None));
    }
}
