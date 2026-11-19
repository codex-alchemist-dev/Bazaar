using Bazaar.Server.Constellation.Memory;
using Bazaar.Server.Memory.Access;
using Bazaar.Server.Memory.GitVault;
using Bazaar.Server.Memory.Tools;
using Bazaar.Server.Memory.Write;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.Tests.Memory.Notes;
using Bazaar.Server.Tests.Vault.Git;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Tests.Memory.GitVault;

/// <summary>Composition of the memory component: off without a path, fail-fast on a half configuration, and the real write path when set.</summary>
public sealed class MemoryComponentTests : IDisposable
{
    private const string _notePath = "agents/alpha/memory/lessons/retry-backoff.md";

    private readonly TempGitRepository _repo = new(("Index.md", "# Vault\n"));
    private readonly List<string> _cleanup = [];

    public void Dispose()
    {
        _repo.Dispose();
        foreach (var dir in _cleanup.Where(Directory.Exists))
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Nothing_is_registered_without_a_repo_path()
    {
        var services = new ServiceCollection();

        new MemoryComponent().AddServices(services, Config());

        Assert.Empty(services);
    }

    [Theory]
    [InlineData(MemoryComponent.WriterNameKey)]
    [InlineData(MemoryComponent.WriterEmailKey)]
    [InlineData(MemoryComponent.AuthorEmailFormatKey)]
    public void A_missing_required_key_fails_fast(string missing)
    {
        var values = FullConfig();
        values.Remove(missing);

        var error = Assert.Throws<InvalidOperationException>(() => new MemoryComponent().AddServices(new ServiceCollection(), Config(values)));

        Assert.Contains(missing, error.Message);
    }

    [Fact]
    public void An_author_format_without_the_agent_token_is_rejected()
    {
        var values = FullConfig();
        values[MemoryComponent.AuthorEmailFormatKey] = "bot@example.invalid";

        Assert.Throws<InvalidOperationException>(() => new MemoryComponent().AddServices(new ServiceCollection(), Config(values)));
    }

    [Fact]
    public async Task The_memory_sink_commits_to_the_vault_and_reads_back()
    {
        using var provider = Build(graph: null);

        var result = await provider.GetRequiredService<IMemoryBatchSink>().CommitAsync(Batch(), CancellationToken.None);

        Assert.True(result.Succeeded, result.FailureReason);
        var text = await provider.GetRequiredService<IMemoryVault>().ReadNoteAsync(VaultPath.Parse(_notePath), CancellationToken.None);
        Assert.NotNull(text);
    }

    [Fact]
    public async Task With_a_graph_store_the_sink_also_projects_the_note()
    {
        var graph = new RecordingGraphStore();
        using var provider = Build(graph);

        var result = await provider.GetRequiredService<IMemoryBatchSink>().CommitAsync(Batch(), CancellationToken.None);

        Assert.True(result.Succeeded, result.FailureReason);
        var revision = Assert.Single(graph.Stored);
        Assert.Equal(NoteFixtures.NoteId, revision.NoteId);
        Assert.Equal("alpha", revision.ScopeAgentId);
    }

    [Fact]
    public async Task With_an_audit_log_the_projected_note_is_audited()
    {
        var audit = new RecordingAudit();
        using var provider = Build(new RecordingGraphStore(), audit);

        var result = await provider.GetRequiredService<IMemoryBatchSink>().CommitAsync(Batch(), CancellationToken.None);

        Assert.True(result.Succeeded, result.FailureReason);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal(AuditingMemoryGraphStore.StoredKind, entry.Kind);
        Assert.Equal("agent:alpha", entry.Actor);
    }

    [Fact]
    public async Task Without_a_vault_id_search_has_no_candidates()
    {
        using var provider = Build(graph: null);

        var hits = await provider.GetRequiredService<IMemoryVault>().SearchLexicalAsync("backoff", "agents/alpha/memory/", 5, CancellationToken.None);

        Assert.Empty(hits);
    }

    private ServiceProvider Build(IMemoryGraphStore? graph, IAuditLog? audit = null)
    {
        var values = FullConfig();
        var worktrees = values[MemoryComponent.WorktreeRootKey]!;
        _cleanup.Add(worktrees);
        var services = new ServiceCollection();
        services.AddLogging();
        if (graph is not null)
        {
            services.AddSingleton(graph);
        }

        if (audit is not null)
        {
            services.AddSingleton(audit);
        }

        new MemoryComponent().AddServices(services, Config(values));
        return services.BuildServiceProvider();
    }

    private Dictionary<string, string?> FullConfig() => new()
    {
        [MemoryComponent.RepoPathKey] = _repo.Checkout,
        [MemoryComponent.WorktreeRootKey] = Path.Combine(Path.GetTempPath(), "bazaar-memcomp-" + Guid.NewGuid().ToString("N")),
        [MemoryComponent.WriterNameKey] = "Test Writer",
        [MemoryComponent.WriterEmailKey] = "writer@example.invalid",
        [MemoryComponent.AuthorEmailFormatKey] = "{agent}@example.invalid",
    };

    private static IConfiguration Config(Dictionary<string, string?>? values = null) =>
        new ConfigurationBuilder().AddInMemoryCollection(values ?? []).Build();

    private static MemoryBatch Batch() =>
        MemoryBatch.Create("alpha", [new MemoryWriteItem(Guid.NewGuid(), _notePath, NoteFixtures.Text(), null, null, "ok")]);

    private sealed class RecordingAudit : IAuditLog
    {
        public List<AuditEntry> Entries { get; } = [];

        public Task AppendAsync(AuditEntry e, CancellationToken ct = default)
        {
            Entries.Add(e);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingGraphStore : IMemoryGraphStore
    {
        public List<MemoryGraphRevision> Stored { get; } = [];

        public Task StoreRevisionAsync(MemoryGraphRevision revision, CancellationToken ct)
        {
            Stored.Add(revision);
            return Task.CompletedTask;
        }

        public Task<bool> ActivateRevisionAsync(string noteId, long version, CancellationToken ct) => Task.FromResult(false);

        public Task<IReadOnlyList<MemoryGraphRevision>> GetActiveAsync(IReadOnlyCollection<string> noteIds, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<MemoryGraphRevision>>([]);

        public Task<IReadOnlyList<MemoryGraphRevision>> ListRevisionsAsync(string noteId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<MemoryGraphRevision>>([]);
    }
}
