using Bazaar.Server.Council.Service;
using Bazaar.Server.Host.Composition;
using Bazaar.Server.Host.CouncilVault;
using Bazaar.Server.Vault.Git;
using Bazaar.Server.Vault.Writes;
using LibGit2Sharp;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Host.Tests.CouncilVault;

/// <summary>The Council's Vault decision adapter (C24): body only in, one VaultWriter-owned record out.</summary>
public sealed class CouncilDecisionVaultAdapterTests
{
    private static readonly Guid _voteId = Guid.Parse("0196a7c4-0000-7000-8000-0000000000a1");

    /// <summary>The Council's own markdown (CouncilService.DecisionMarkdown): a front-matter block, then the body.</summary>
    private static string CouncilMarkdown(Guid voteId) => $"""
        ---
        vote_id: {voteId:D}
        gc_id: 0196a7c4-0000-7000-8000-0000000000b2
        kind: approve_reject
        motion_class: ordinary
        decided_by: tally
        choice: yes
        ---

        # Council decision {voteId:D}

        Subject: [[hire-candidate]]
        Choice: yes (tally)
        Final tally: no: 1; yes: 2
        """;

    [Fact]
    public async Task The_writer_gets_the_vote_id_a_council_title_and_the_body_without_the_council_front_matter()
    {
        var recorder = new RecordingCreate(3);
        var adapter = new CouncilDecisionVaultAdapter(recorder.Create);

        var number = await adapter.WriteDecisionAsync(_voteId, "council-vote", CouncilMarkdown(_voteId), CancellationToken.None);

        Assert.Equal(3, number);
        var draft = Assert.Single(recorder.Drafts);
        Assert.Equal(_voteId, draft.DocId);
        Assert.Equal("council-vote", draft.Slug);
        Assert.Equal($"Council decision {_voteId:D}", draft.Title);
        Assert.Equal(CouncilDecisionVaultAdapter.CouncilAgentId, draft.AgentId);
        Assert.StartsWith($"# Council decision {_voteId:D}", draft.Body);
        Assert.DoesNotContain("vote_id:", draft.Body);
        Assert.DoesNotContain("---", draft.Body);
        Assert.Contains("Subject: [[hire-candidate]]", draft.Body);
    }

    [Fact]
    public async Task A_body_without_front_matter_is_passed_unchanged()
    {
        var recorder = new RecordingCreate(1);
        var adapter = new CouncilDecisionVaultAdapter(recorder.Create);
        const string body = "# Council decision\n\nChoice: no\n";

        await adapter.WriteDecisionAsync(_voteId, "council-vote", body, CancellationToken.None);

        Assert.Equal(body, Assert.Single(recorder.Drafts).Body);
    }

    [Fact]
    public async Task A_writer_result_without_a_number_is_a_failure_not_a_zero()
    {
        var adapter = new CouncilDecisionVaultAdapter((draft, _) => Task.FromResult(
            new VaultEditResult(VaultEditOutcome.Merged, "a", "b", Array.Empty<string>(), null)));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.WriteDecisionAsync(_voteId, "council-vote", CouncilMarkdown(_voteId), CancellationToken.None));
    }

    [Theory]
    [InlineData("---\nk: v\n---\n\n# Head\n", "# Head\n")]
    [InlineData("---\r\nk: v\r\n---\r\n\r\n# Head\r\n", "# Head\r\n")]
    [InlineData("---\nk: v\n---\n# Head", "# Head")]
    [InlineData("# Head\n\n---\n", "# Head\n\n---\n")]
    [InlineData("---\nunterminated: yes\n", "---\nunterminated: yes\n")]
    [InlineData("", "")]
    public void Front_matter_is_stripped_only_when_a_closed_block_leads_the_text(string markdown, string expected) =>
        Assert.Equal(expected, CouncilDecisionVaultAdapter.StripFrontMatter(markdown));

    [Fact]
    public async Task The_real_vault_writer_writes_one_front_matter_block_and_a_repeat_adds_no_record()
    {
        var root = Path.Combine(Path.GetTempPath(), "bazaar-council-vault-" + Guid.NewGuid().ToString("N"));
        try
        {
            var checkout = InitRepository(root);
            var writer = new VaultWriter(new LibGit2GitProvider(), checkout, new GitIdentity("Vault", "vault@example.invalid"));
            var adapter = new CouncilDecisionVaultAdapter(writer.CreateDecisionAsync);

            var first = await adapter.WriteDecisionAsync(_voteId, "council-vote", CouncilMarkdown(_voteId), CancellationToken.None);
            var second = await adapter.WriteDecisionAsync(_voteId, "council-vote", CouncilMarkdown(_voteId), CancellationToken.None);

            Assert.Equal(1, first);
            Assert.Equal(first, second);
            var files = Directory.GetFiles(Path.Combine(checkout, "decisions"), "*.md");
            var file = Assert.Single(files);
            var text = File.ReadAllText(file).Replace("\r\n", "\n");
            Assert.StartsWith("---\nid: ", text);
            // One front-matter block: the opening and closing fence only. The Council's own block is gone.
            Assert.Equal(2, CountLines(text, "---"));
            Assert.Contains("type: decision\n", text);
            Assert.Contains($"# Council decision {_voteId:D}", text);
            Assert.DoesNotContain("vote_id:", text);
        }
        finally
        {
            DeleteTree(root);
        }
    }

    // Git object files are read-only on Windows; clear the attribute before deleting.
    private static void DeleteTree(string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void The_host_registers_the_council_decision_port_as_a_singleton()
    {
        var services = new ServiceCollection();

        services.AddBazaarCouncilDecisionVault();

        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(ICouncilDecisionWriter));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
        Assert.NotNull(descriptor.ImplementationFactory);
    }

    /// <summary>Records the drafts it receives and returns a fixed number, as the Vault would for a new record.</summary>
    private sealed class RecordingCreate(int number)
    {
        public List<DecisionDraft> Drafts { get; } = [];

        public Task<VaultEditResult> Create(DecisionDraft draft, CancellationToken ct)
        {
            Drafts.Add(draft);
            return Task.FromResult(new VaultEditResult(VaultEditOutcome.Merged, "a", "b", Array.Empty<string>(), number));
        }
    }

    private static int CountLines(string text, string line) =>
        text.Split('\n').Count(l => l == line);

    /// <summary>A repository with one commit on main, so the writer has a HEAD to commit on.</summary>
    private static string InitRepository(string root)
    {
        var checkout = Path.Combine(root, "main");
        Directory.CreateDirectory(checkout);
        using var repo = new Repository(Repository.Init(checkout));
        repo.Refs.UpdateTarget("HEAD", "refs/heads/main");
        File.WriteAllText(Path.Combine(checkout, "README.md"), "vault\n");
        Commands.Stage(repo, "*");
        var signature = new Signature("Test Writer", "writer@example.invalid", DateTimeOffset.UtcNow);
        repo.Commit("initial", signature, signature);
        return checkout;
    }
}
