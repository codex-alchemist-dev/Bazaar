using Bazaar.Server.Constellation.Memory;
using Bazaar.Server.Constellation.Memory.ReadLog;
using Bazaar.Server.Memory.Access;
using Bazaar.Server.Memory.Retrieval;

namespace Bazaar.Server.Constellation.Tests.Memory.ReadLog;

/// <summary>The retriever's use of the optional <see cref="IMemoryReadLog"/> port (constellation-memory-graph.md, "Owner-read access log").</summary>
public class MemoryReadLogRetrieverTests
{
    private static readonly VaultPathAccess _access = new(new StaticProjectMembership(new Dictionary<string, ProjectMembers>()));
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task No_log_registered_means_no_logging_and_the_owner_read_succeeds()
    {
        var nodes = new Nodes(Note("a1", "agent-a"));
        var retriever = new MemoryGraphRetriever(nodes, _access);

        var result = await RetrieveAsync(retriever, VaultPrincipal.Owner(), nodes, "a1");

        Assert.Single(result.Hits);
    }

    [Fact]
    public async Task Owner_read_of_another_agents_notes_is_logged_with_ids_only_before_the_result_returns()
    {
        var nodes = new Nodes(Note("a1", "agent-a"), Note("a2", "agent-a"));
        var log = new RecordingLog();
        var retriever = new MemoryGraphRetriever(nodes, _access, readLog: log);

        var result = await RetrieveAsync(retriever, VaultPrincipal.Owner(), nodes, "a1", "a2");

        Assert.Equal(2, result.Hits.Count);
        MemoryReadEntry entry = Assert.Single(Assert.Single(log.Batches));
        Assert.Equal("owner", entry.ReaderPrincipal);
        Assert.Equal("agent-a", entry.SubjectAgentId);
        Assert.Equal(MemoryReadOperations.Retrieve, entry.Operation);
        Assert.Equal(MemoryReadContext.DefaultRoute, entry.Route);
        Assert.Equal(["a1", "a2"], entry.NoteIds.Order().ToArray());
        Assert.Equal(result.Hits.Select(h => h.DocId).Order().ToArray(), entry.DocIds.Order().ToArray());
    }

    [Fact]
    public async Task Caller_context_is_carried_into_the_entry()
    {
        var nodes = new Nodes(Note("a1", "agent-a"));
        var log = new RecordingLog();
        var retriever = new MemoryGraphRetriever(nodes, _access, readLog: log);

        await RetrieveAsync(
            retriever,
            VaultPrincipal.Owner(),
            nodes,
            new MemoryReadContext("owner:boss", "sess-1", "memory.context", "audit review", ["labelled_quoted_data"]),
            "a1");

        MemoryReadEntry entry = Assert.Single(Assert.Single(log.Batches));
        Assert.Equal("owner:boss", entry.ReaderPrincipal);
        Assert.Equal("sess-1", entry.ReaderSession);
        Assert.Equal("memory.context", entry.Route);
        Assert.Equal("audit review", entry.Purpose);
        Assert.Equal(["labelled_quoted_data"], entry.TaintTags);
    }

    [Fact]
    public async Task Agent_reading_its_own_area_is_not_logged()
    {
        var nodes = new Nodes(Note("a1", "agent-a"));
        var log = new RecordingLog();
        var retriever = new MemoryGraphRetriever(nodes, _access, readLog: log);

        var result = await RetrieveAsync(retriever, VaultPrincipal.Agent("agent-a"), nodes, "a1");

        Assert.Single(result.Hits);
        Assert.Empty(log.Batches);
    }

    [Fact]
    public async Task Maintenance_read_of_private_memory_is_logged_under_its_principal()
    {
        var nodes = new Nodes(Note("a1", "agent-a"));
        var log = new RecordingLog();
        var retriever = new MemoryGraphRetriever(nodes, _access, readLog: log);

        await RetrieveAsync(retriever, VaultPrincipal.Maintenance(), nodes, "a1");

        Assert.Equal("maintenance", Assert.Single(Assert.Single(log.Batches)).ReaderPrincipal);
    }

    [Fact]
    public async Task One_entry_per_subject_agent()
    {
        var nodes = new Nodes(Note("a1", "agent-a"), Note("b1", "agent-b"));
        var log = new RecordingLog();
        var retriever = new MemoryGraphRetriever(nodes, _access, readLog: log);

        await RetrieveAsync(retriever, VaultPrincipal.Owner(), nodes, "a1", "b1");

        Assert.Equal(["agent-a", "agent-b"], Assert.Single(log.Batches).Select(e => e.SubjectAgentId!).Order().ToArray());
    }

    [Fact]
    public async Task Empty_result_is_not_logged()
    {
        var nodes = new Nodes();
        var log = new RecordingLog();
        var retriever = new MemoryGraphRetriever(nodes, _access, readLog: log);

        var result = await RetrieveAsync(retriever, VaultPrincipal.Owner(), nodes);

        Assert.Empty(result.Hits);
        Assert.Empty(log.Batches);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("maintenance")]
    [InlineData("member")]
    public async Task Failing_log_fails_the_read_closed(string kind)
    {
        var nodes = new Nodes(Note("a1", "agent-a"));
        var retriever = new MemoryGraphRetriever(nodes, _access, readLog: new RecordingLog(fail: new InvalidOperationException("db down")));
        VaultPrincipal who = kind switch
        {
            "owner" => VaultPrincipal.Owner(),
            "maintenance" => VaultPrincipal.Maintenance(),
            _ => VaultPrincipal.MemberHuman("member-1"),
        };

        // The member cannot read private memory, so only the principals that can read it reach the log; a member's empty read is not logged.
        if (kind == "member")
        {
            var result = await RetrieveAsync(retriever, who, nodes, "a1");
            Assert.Empty(result.Hits);
            return;
        }

        await Assert.ThrowsAsync<MemoryReadLogException>(() => RetrieveAsync(retriever, who, nodes, "a1"));
    }

    [Fact]
    public async Task Log_exception_type_is_preserved_for_the_query_path_too()
    {
        var nodes = new Nodes(Note("a1", "agent-a"));
        var retriever = new MemoryGraphRetriever(nodes, _access, readLog: new RecordingLog(fail: new MemoryReadLogException("no")));

        await Assert.ThrowsAsync<MemoryReadLogException>(() => retriever.RetrieveForQueryAsync(
            VaultPrincipal.Owner(),
            "q",
            [new RankedHit(Key("a1"), 1)],
            (ids, _) => Task.FromResult<IReadOnlyDictionary<string, int>>(ids.ToDictionary(id => id, _ => 1)),
            1000,
            new MemoryGraphOptions(MaxHops: 0),
            vectorCandidates: 5,
            Ct));
    }

    private static Task<MemoryGraphRetrieval> RetrieveAsync(
        MemoryGraphRetriever retriever, VaultPrincipal who, Nodes nodes, params string[] names) =>
        RetrieveAsync(retriever, who, nodes, null, names);

    private static Task<MemoryGraphRetrieval> RetrieveAsync(
        MemoryGraphRetriever retriever, VaultPrincipal who, Nodes nodes, MemoryReadContext? context, params string[] names) =>
        retriever.RetrieveAsync(
            who,
            [.. names.Select((n, i) => new RankedHit(Key(n), names.Length - i))],
            [],
            nodes.Ids().ToDictionary(id => id, _ => 1, StringComparer.Ordinal),
            1000,
            new MemoryGraphOptions(MaxHops: 0),
            Ct,
            context);

    private static readonly Dictionary<string, Guid> _ids = new(StringComparer.Ordinal);

    private static Guid DocId(string name)
    {
        lock (_ids)
        {
            if (!_ids.TryGetValue(name, out Guid id))
            {
                _ids[name] = id = Guid.CreateVersion7();
            }

            return id;
        }
    }

    private static string Key(string name) => DocId(name).ToString("D");

    private static ConstellationGraphNode Note(string name, string scope)
    {
        string path = $"agents/{scope}/memory/{name}.md";
        var memory = new MemoryGraphRevision(name, 1, scope, path, name, "Semantic", "Active", "Trusted", [], DocId(name));
        return new ConstellationGraphNode(DocId(name), path, name, "memory", null, memory, []);
    }

    private sealed class Nodes(params ConstellationGraphNode[] nodes) : IConstellationNodeReader
    {
        private readonly Dictionary<Guid, ConstellationGraphNode> _nodes = nodes.ToDictionary(n => n.DocId);

        public string[] Ids() => [.. _nodes.Keys.Select(id => id.ToString("D"))];

        public Task<IReadOnlyList<ConstellationGraphNode>> GetNodesAsync(IReadOnlyCollection<Guid> docIds, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ConstellationGraphNode>>([.. docIds.Where(_nodes.ContainsKey).Select(id => _nodes[id])]);
    }

    private sealed class RecordingLog(Exception? fail = null) : IMemoryReadLog
    {
        public List<IReadOnlyList<MemoryReadEntry>> Batches { get; } = [];

        public Task RecordAsync(IReadOnlyList<MemoryReadEntry> entries, CancellationToken ct)
        {
            if (fail is not null)
            {
                throw fail;
            }

            Batches.Add(entries);
            return Task.CompletedTask;
        }
    }
}
