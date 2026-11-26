using System.Text.Json;
using Bazaar.Contracts.Memory;
using Bazaar.Contracts.Wire;
using Bazaar.Contracts.Wire.Codec;
using Bazaar.Server.Constellation.Memory;
using Bazaar.Server.Constellation.Memory.Context;
using Bazaar.Server.Memory.Access;
using Bazaar.Server.Memory.Tools;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.Vault.Search;
using Bazaar.Server.Wire.Connection;
using Bazaar.Server.Wire.Sync;
using Bazaar.Testing.Postgres;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Tests.Constellation.Memory;

/// <summary>
/// The retrieval operation on real PostgreSQL (docs/design/constellation-memory-graph.md, "Retrieval operation"): authorisation from the
/// session, limits, packing with provenance, isolation, the owner access log hook, and the <c>memory.context.retrieved</c> audit entry.
/// </summary>
public sealed class MemoryContextServiceTests(ConstellationDb db) : IClassFixture<ConstellationDb>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    [Fact]
    public async Task Agent_gets_its_own_note_packed_with_provenance_and_one_audit_entry_with_ids_only()
    {
        SkipWithoutDatabase();
        var f = await Fixture.CreateAsync(db);
        var (doc, note) = await f.NoteAsync("agent-a", "retry", $"Retry deploys {f.Word} three times with backoff.");
        var audit = new RecordingAudit();

        var outcome = await f.Service(audit).RetrieveAsync(MemoryContextCaller.Agent("agent-a", "sess-1"), Fixture.Request("agent-a", $"{f.Word} deploys"), Ct);

        Assert.True(outcome.IsSuccess, outcome.Message);
        var result = outcome.Result!;
        var hit = Assert.Single(result.Hits);
        Assert.Equal(doc.ToString("D"), hit.DocId);
        Assert.Equal(note, hit.NoteId);
        Assert.Equal("private", hit.Scope);
        Assert.Equal("T2", hit.Tier);
        Assert.Equal("agent:agent-a", hit.Origin);
        Assert.Equal(1L, hit.Version);
        Assert.False(hit.Labelled);
        Assert.Contains("backoff", hit.BodyExcerpt);
        Assert.True(result.LexicalOnly);
        Assert.Equal(MemoryLexicalOnlyReasons.ProfileNone, result.LexicalOnlyReason);
        Assert.Equal(result.TokensUsed, (hit.BodyExcerpt.Length + 3) / 4);

        var entry = Assert.Single(audit.Entries);
        Assert.Equal(MemoryContextService.AuditKind, entry.Kind);
        Assert.Equal("agent:agent-a", entry.Actor);
        using var payload = JsonDocument.Parse(entry.Payload);
        Assert.Equal(result.AuditId, payload.RootElement.GetProperty("call_id").GetString());
        Assert.Equal(1, payload.RootElement.GetProperty("result_count").GetInt32());
        Assert.Equal("profile_none", payload.RootElement.GetProperty("lexical_only_reason").GetString());
        Assert.Equal(note, payload.RootElement.GetProperty("note_ids")[0].GetString());
        Assert.DoesNotContain(f.Word, entry.Payload, StringComparison.Ordinal);
        Assert.DoesNotContain("agents/", entry.Payload, StringComparison.Ordinal);
        Assert.DoesNotContain("backoff", entry.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_body_agent_id_that_differs_from_the_session_is_unauthorized_and_reads_nothing()
    {
        SkipWithoutDatabase();
        var f = await Fixture.CreateAsync(db);
        await f.NoteAsync("agent-b", "secret", $"Secret {f.Word}.");
        var audit = new RecordingAudit();

        var outcome = await f.Service(audit).RetrieveAsync(MemoryContextCaller.Agent("agent-a"), Fixture.Request("agent-b", f.Word), Ct);

        Assert.Equal(MemoryContextErrorCodes.Unauthorized, outcome.ErrorCode);
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task Another_agents_private_note_never_appears_as_seed_or_hop_target()
    {
        SkipWithoutDatabase();
        var f = await Fixture.CreateAsync(db);
        var (mine, _) = await f.NoteAsync("agent-a", "mine", $"Mine {f.Word}.");
        var (theirs, _) = await f.NoteAsync("agent-b", "theirs", $"Theirs {f.Word}.");
        await f.Seed.LinkAsync(mine, "Theirs", theirs);

        var outcome = await f.Service(new RecordingAudit()).RetrieveAsync(MemoryContextCaller.Agent("agent-a"), Fixture.Request("agent-a", f.Word), Ct);

        Assert.Equal([mine.ToString("D")], outcome.Result!.Hits.Select(h => h.DocId));
    }

    [Fact]
    public async Task Owner_read_of_an_agents_private_area_is_logged_through_the_access_log_and_audited_as_the_owner()
    {
        SkipWithoutDatabase();
        var f = await Fixture.CreateAsync(db);
        var (doc, _) = await f.NoteAsync("agent-b", "private", $"Private {f.Word}.");
        var audit = new RecordingAudit();
        var log = new RecordingAccessLog();

        var outcome = await f.Service(audit, accessLog: log).RetrieveAsync(MemoryContextCaller.Owner("owner:1"), Fixture.Request("agent-b", f.Word), Ct);

        Assert.Equal([doc.ToString("D")], outcome.Result!.Hits.Select(h => h.DocId));
        var read = Assert.Single(log.Entries);
        Assert.Equal($"agents/agent-b/memory/{f.Seed.Tag}-private.md", read.Path);
        Assert.Equal("owner:1", Assert.Single(audit.Entries).Actor);

        var refused = await f.Service(new RecordingAudit(), accessLog: new RecordingAccessLog(fail: true))
            .RetrieveAsync(MemoryContextCaller.Owner("owner:1"), Fixture.Request("agent-b", f.Word), Ct);
        Assert.Equal(MemoryContextErrorCodes.Unavailable, refused.ErrorCode);
    }

    [Fact]
    public async Task Limits_refuse_long_queries_and_over_rate_calls_and_clamp_budgets()
    {
        SkipWithoutDatabase();
        var f = await Fixture.CreateAsync(db);
        await f.NoteAsync("agent-a", "one", $"One {f.Word}.");
        await f.NoteAsync("agent-a", "two", $"Two {f.Word}.");
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var service = f.Service(new RecordingAudit(), new MemoryContextLimits(MaxTokens: 2000, MaxHits: 1, MaxQueryChars: 40, RatePerAgentPerMinute: 2), time);
        var caller = MemoryContextCaller.Agent("agent-a");

        Assert.Equal(MemoryContextErrorCodes.TooLarge, (await service.RetrieveAsync(caller, Fixture.Request("agent-a", new string('x', 41)), Ct)).ErrorCode);
        Assert.Equal(MemoryContextErrorCodes.BadRequest, (await service.RetrieveAsync(caller, Fixture.Request("agent-a", "  "), Ct)).ErrorCode);

        var first = await service.RetrieveAsync(caller, Fixture.Request("agent-a", f.Word) with { MaxHits = 10 }, Ct);
        Assert.Single(first.Result!.Hits);
        Assert.True(first.Result.Truncated);
        Assert.True((await service.RetrieveAsync(caller, Fixture.Request("agent-a", f.Word), Ct)).IsSuccess);
        Assert.Equal(MemoryContextErrorCodes.RateLimited, (await service.RetrieveAsync(caller, Fixture.Request("agent-a", f.Word), Ct)).ErrorCode);

        time.Advance(TimeSpan.FromMinutes(1));
        Assert.True((await service.RetrieveAsync(caller, Fixture.Request("agent-a", f.Word), Ct)).IsSuccess);
    }

    [Fact]
    public async Task Already_in_context_ids_are_dropped_t1_is_labelled_and_t0_is_never_packed()
    {
        SkipWithoutDatabase();
        var f = await Fixture.CreateAsync(db);
        var (known, knownNote) = await f.NoteAsync("agent-a", "known", $"Known {f.Word}.");
        var (tainted, _) = await f.NoteAsync("agent-a", "tainted", $"Tainted {f.Word}\nsecond line", trust: "UntrustedDerived");
        await f.NoteAsync("agent-a", "quarantined", $"Quarantined {f.Word}.", trust: "Quarantined");

        var outcome = await f.Service(new RecordingAudit()).RetrieveAsync(
            MemoryContextCaller.Agent("agent-a"),
            Fixture.Request("agent-a", f.Word) with { AlreadyInContext = [knownNote] },
            Ct);

        var hit = Assert.Single(outcome.Result!.Hits);
        Assert.Equal(tainted.ToString("D"), hit.DocId);
        Assert.True(hit.Labelled);
        Assert.Equal("T1", hit.Tier);
        Assert.StartsWith("> Tainted", hit.BodyExcerpt);
        Assert.Contains("\n> second line", hit.BodyExcerpt);
        Assert.DoesNotContain(outcome.Result.Hits, h => h.DocId == known.ToString("D"));
    }

    [Fact]
    public async Task Project_scope_adds_the_projects_shared_area_and_vector_seeds_join_through_the_query_embedder()
    {
        SkipWithoutDatabase();
        var f = await Fixture.CreateAsync(db);
        var project = "p" + f.Seed.Tag;
        var decision = await f.Seed.DocAsync($"projects/{project}/decisions/retry.md", "decision", $"Decision {f.Seed.Tag}");
        await f.SetBodyAsync(decision, "We decided on exponential backoff.");
        var profile = Guid.CreateVersion7();
        await f.Seed.ExecAsync("INSERT INTO embedding_profile (profile_id, provider_id, model_id, dimensions) VALUES ($1, 'fake', $2, 8)",
            profile, "fake-model");
        var embedder = new HashEmbedder(profile, 8);
        var query = $"{f.Word} policy";
        await f.Seed.ExecAsync(
            "INSERT INTO vault_block_embedding (doc_id, ordinal, profile_id, text_hash, embedding) VALUES ($1, 0, $2, $3, $4::text::halfvec)",
            decision, profile, new byte[32], PgMemoryVectorQuery.ToVectorLiteral(await embedder.EmbedAsync(query, Ct)));
        var membership = new StaticProjectMembership(new Dictionary<string, ProjectMembers>
        {
            [project] = new(new HashSet<string> { "agent-a" }, new HashSet<string>()),
        });
        var service = f.Service(new RecordingAudit(), access: new VaultPathAccess(membership), embedder: embedder);

        var privateOnly = await service.RetrieveAsync(MemoryContextCaller.Agent("agent-a"), Fixture.Request("agent-a", query), Ct);
        var withProject = await service.RetrieveAsync(MemoryContextCaller.Agent("agent-a"), Fixture.Request("agent-a", query) with { ProjectScope = project }, Ct);

        Assert.Empty(privateOnly.Result!.Hits);
        Assert.Equal(MemoryLexicalOnlyReasons.NoVectorsYet, privateOnly.Result.LexicalOnlyReason);
        var hit = Assert.Single(withProject.Result!.Hits);
        Assert.Equal(decision.ToString("D"), hit.DocId);
        Assert.Equal("project", hit.Scope);
        Assert.Equal("vault", hit.Origin);
        Assert.False(withProject.Result.LexicalOnly);
    }

    [Fact]
    public async Task A_read_that_cannot_be_audited_is_refused()
    {
        SkipWithoutDatabase();
        var f = await Fixture.CreateAsync(db);
        await f.NoteAsync("agent-a", "one", $"One {f.Word}.");

        var outcome = await f.Service(new RecordingAudit(fail: true)).RetrieveAsync(MemoryContextCaller.Agent("agent-a"), Fixture.Request("agent-a", f.Word), Ct);

        Assert.Equal(MemoryContextErrorCodes.Unavailable, outcome.ErrorCode);
        Assert.Null(outcome.Result);
    }

    [Fact]
    public async Task Wire_command_serves_the_owner_and_refuses_other_principals()
    {
        SkipWithoutDatabase();
        var f = await Fixture.CreateAsync(db);
        var (doc, _) = await f.NoteAsync("agent-a", "wired", $"Wired {f.Word}.");
        var audit = new RecordingAudit();
        var handler = new MemoryContextWireHandler(f.Service(audit));
        var codec = new JsonWireCodec();

        var ownerReply = await handler.HandleAsync(Request(new FakeConnection(new PrincipalRef(Guid.CreateVersion7(), "owner"), codec), codec, Fixture.Request("agent-a", f.Word)), Ct);
        var agentReply = await handler.HandleAsync(Request(new FakeConnection(new PrincipalRef(Guid.CreateVersion7(), "agent"), codec), codec, Fixture.Request("agent-a", f.Word)), Ct);

        Assert.True(ownerReply.IsOk, ownerReply.Error?.Message);
        var result = codec.DecodePayload<MemoryContextResult>(new Envelope(1, WireTypes.MemoryContext, EnvelopeKind.Response, "r", "r", null, null, null, ownerReply.Payload));
        Assert.Equal([doc.ToString("D")], result.Hits.Select(h => h.DocId));
        Assert.StartsWith("owner:", Assert.Single(audit.Entries).Actor, StringComparison.Ordinal);
        Assert.False(agentReply.IsOk);
        Assert.Equal(MemoryContextErrorCodes.Unauthorized, agentReply.Error!.Code);
        Assert.Equal(WireTypes.MemoryContext, handler.Type);
    }

    private static WireRequestContext Request(IWireConnection connection, JsonWireCodec codec, MemoryContextRequest request) =>
        new(connection, new Envelope(1, WireTypes.MemoryContext, EnvelopeKind.Request, "r", null, null, null, null, codec.EncodePayload(request)));

    /// <summary>One test's Vault rows: docs carry a fresh word so full-text queries see only this test's docs.</summary>
    private sealed class Fixture(ConstellationDb db, GraphSeed seed)
    {
        public GraphSeed Seed { get; } = seed;

        public string Word { get; } = "zq" + seed.Tag;

        public static Task<Fixture> CreateAsync(ConstellationDb db) => Task.FromResult(new Fixture(db, new GraphSeed(db.DataSource)));

        public static MemoryContextRequest Request(string agentId, string query) => new(agentId, query, null, 0, 0, null);

        public async Task<(Guid Doc, string Note)> NoteAsync(string agent, string name, string body, string trust = "AgentVerified")
        {
            var path = $"agents/{agent}/memory/{Seed.Tag}-{name}.md";
            var doc = await Seed.DocAsync(path, "memory", $"{name} {Seed.Tag}", owner: agent);
            await SetBodyAsync(doc, body);
            var note = GraphSeed.NoteId();
            await new PgMemoryGraphStore(db.DataSource).StoreRevisionAsync(
                new MemoryGraphRevision(note, 1, agent, path, name, "Semantic", "Active", trust, []), Ct);
            return (doc, note);
        }

        public Task SetBodyAsync(Guid doc, string body) => Seed.ExecAsync("UPDATE vault_blocks SET body = $2 WHERE doc_id = $1", doc, body);

        public MemoryContextService Service(
            IAuditLog audit,
            MemoryContextLimits? limits = null,
            TimeProvider? time = null,
            IMemoryAccessLog? accessLog = null,
            IVaultAccess? access = null,
            IVaultQueryEmbedder? embedder = null)
        {
            access ??= new VaultPathAccess(new StaticProjectMembership(new Dictionary<string, ProjectMembers>()));
            IMemoryVectorSeedSource? seeds = embedder is null
                ? null
                : new EmbeddingMemoryVectorSeedSource(embedder, new PgMemoryVectorQuery(db.DataSource), access, Seed.VaultId);
            var retriever = new MemoryGraphRetriever(new PgConstellationNodeReader(db.DataSource, Seed.VaultId), access, seeds);
            return new MemoryContextService(
                retriever,
                new PgMemoryContextReader(db.DataSource),
                access,
                audit,
                new FixedMemoryContextLimits(limits ?? MemoryContextLimits.Defaults),
                time,
                accessLog: accessLog,
                vaultId: Seed.VaultId);
        }
    }

    private sealed class RecordingAudit(bool fail = false) : IAuditLog
    {
        public List<AuditEntry> Entries { get; } = [];

        public Task AppendAsync(AuditEntry e, CancellationToken ct = default)
        {
            if (fail)
            {
                return Task.FromException(new InvalidOperationException("audit down"));
            }

            Entries.Add(e);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingAccessLog(bool fail = false) : IMemoryAccessLog
    {
        public List<MemoryAccessEntry> Entries { get; } = [];

        public void Record(MemoryAccessEntry entry)
        {
            if (fail)
            {
                throw new InvalidOperationException("log down");
            }

            Entries.Add(entry);
        }
    }

    private sealed class HashEmbedder(Guid profileId, int dims) : IVaultQueryEmbedder
    {
        public Guid ProfileId { get; } = profileId;

        public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken)
        {
            var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text));
            var vector = new float[dims];
            for (var i = 0; i < dims; i++)
            {
                vector[i] = hash[i] - 127.5f;
            }

            return Task.FromResult(vector);
        }
    }

    private sealed class FakeConnection(PrincipalRef principal, IWireCodec codec) : IWireConnection
    {
        public Guid ConnectionId { get; } = Guid.CreateVersion7();
        public PrincipalRef Principal { get; } = principal;
        public string DeviceId => "test";
        public string DeviceClass => "test";
        public IWireCodec Codec => codec;
        public IReadOnlyList<StreamCursor> HelloCursors => [];
        public ISubscriptionSession? Subscriptions => null;
        public CancellationToken Closed => CancellationToken.None;
        public bool TryEnqueueEvent(Envelope envelope) => true;
        public bool TryEnqueueLagged(Lagged lagged) => true;
        public ValueTask CloseAsync(int closeCode, string reason) => ValueTask.CompletedTask;
    }
}
