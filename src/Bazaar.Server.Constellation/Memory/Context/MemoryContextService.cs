using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Bazaar.Contracts.Memory;
using Bazaar.Server.Constellation.Memory.ReadLog;
using Bazaar.Server.Memory.Access;
using Bazaar.Server.Memory.Retrieval;
using Bazaar.Server.Memory.Tools;
using Bazaar.Server.Memory.Trust;
using Bazaar.Server.PrivacyAudit;
using Bazaar.Server.Security.Audit;
using Npgsql;

namespace Bazaar.Server.Constellation.Memory.Context;

/// <summary>
/// Who asks for memory context. The identity comes from the session, never from the request body (docs/design/constellation-memory-graph.md,
/// "Retrieval operation", Authorisation). Use the factories.
/// </summary>
/// <param name="Kind">Agent or Owner. Any other kind is refused.</param>
/// <param name="AgentId">The session's agent id for an agent caller; null for the owner.</param>
/// <param name="Actor">The audit actor, for example <c>agent:ada</c> or <c>owner:&lt;principal id&gt;</c>. Also the rate-limit key.</param>
/// <param name="Session">The session or connection id, for the audit entry; null when there is none.</param>
/// <param name="Route">How the call arrived: <c>memory.context</c> for the wire command, <c>in_process</c> for the agent runtime.</param>
public sealed record MemoryContextCaller(VaultPrincipalKind Kind, string? AgentId, string Actor, string? Session, string Route)
{
    /// <summary>The in-process route name.</summary>
    public const string InProcessRoute = "in_process";

    /// <summary>An agent's own session.</summary>
    public static MemoryContextCaller Agent(string agentId, string? session = null, string route = InProcessRoute)
    {
        if (!VaultPath.IsValidIdentifier(agentId))
        {
            throw new ArgumentException("The agent id must be a single valid path segment.", nameof(agentId));
        }

        return new MemoryContextCaller(VaultPrincipalKind.Agent, agentId, "agent:" + agentId, session, route);
    }

    /// <summary>The owner, with the audit actor of the owner principal.</summary>
    public static MemoryContextCaller Owner(string actor, string? session = null, string route = InProcessRoute)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        return new MemoryContextCaller(VaultPrincipalKind.Owner, null, actor, session, route);
    }
}

/// <summary>The outcome of a memory context call: the result, or a <see cref="MemoryContextErrorCodes"/> code with a message that quotes no content.</summary>
public sealed record MemoryContextOutcome(MemoryContextResult? Result, string? ErrorCode, string? Message)
{
    /// <summary>True when <see cref="Result"/> is set.</summary>
    public bool IsSuccess => Result is not null;

    public static MemoryContextOutcome Ok(MemoryContextResult result) => new(result ?? throw new ArgumentNullException(nameof(result)), null, null);

    public static MemoryContextOutcome Fail(string code, string message) => new(null, code, message);
}

/// <summary>Tuning of the retrieval operation that is not an operator setting. Defaults are the shipped values.</summary>
/// <param name="MaxHops">Graph expansion depth (default 1; the retriever caps it at 2).</param>
/// <param name="TopK">Fused seeds kept before expansion.</param>
/// <param name="SeedCandidates">Blocks taken from each of the lexical and vector queries before collapsing to docs.</param>
/// <param name="ExcerptChars">The longest excerpt per hit, in characters, before T1 quoting.</param>
/// <param name="MaxAlreadyInContext">The most ids accepted in <c>AlreadyInContext</c>.</param>
public sealed record MemoryContextOptions(int MaxHops = 1, int TopK = 20, int SeedCandidates = 50, int ExcerptChars = 1600, int MaxAlreadyInContext = 500);

/// <summary>The retrieval operation a model turn uses to ask for packed memory context.</summary>
public interface IMemoryContextService
{
    /// <summary>Retrieves, packs and audits. Never throws for an embedding failure; refusals come back as an outcome code.</summary>
    Task<MemoryContextOutcome> RetrieveAsync(MemoryContextCaller caller, MemoryContextRequest request, CancellationToken ct);
}

/// <summary>
/// The retrieval operation (docs/design/constellation-memory-graph.md, "Retrieval operation"). Lexical seeds come from the Vault full-text
/// index and vector seeds from the Vault query embedder contract, both restricted to the caller's readable paths and scope before ranking; the graph
/// retriever fuses, expands, applies supersede-head ranking and packs. Quarantined (T0) notes and ids already in context are never packed,
/// T1 excerpts are quoted and labelled, every owner read of another agent's private area goes to <see cref="IMemoryAccessLog"/> when one is
/// registered, and each call writes one <c>memory.context.retrieved</c> audit entry with ids only.
/// </summary>
public sealed class MemoryContextService : IMemoryContextService
{
    /// <summary>The audit kind of one retrieval.</summary>
    public const string AuditKind = "memory.context.retrieved";

    private const string _ownerReader = "owner";

    private readonly MemoryGraphRetriever _retriever;
    private readonly IMemoryContextReader _reader;
    private readonly IVaultAccess _access;
    private readonly IAuditLog _audit;
    private readonly IMemoryContextLimitsSource _limits;
    private readonly IMemoryAccessLog? _accessLog;
    private readonly TimeProvider _time;
    private readonly MemoryContextOptions _options;
    private readonly Guid? _vaultId;
    private readonly SlidingWindowRateLimiter _rate;

    public MemoryContextService(
        MemoryGraphRetriever retriever,
        IMemoryContextReader reader,
        IVaultAccess access,
        IAuditLog audit,
        IMemoryContextLimitsSource limits,
        TimeProvider? time = null,
        MemoryContextOptions? options = null,
        IMemoryAccessLog? accessLog = null,
        Guid? vaultId = null)
    {
        ArgumentNullException.ThrowIfNull(retriever);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(limits);
        _retriever = retriever;
        _reader = reader;
        _access = access;
        _audit = audit;
        _limits = limits;
        _time = time ?? TimeProvider.System;
        _options = options ?? new MemoryContextOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.SeedCandidates, 1, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.ExcerptChars, 16, nameof(options));
        _accessLog = accessLog;
        _vaultId = vaultId;
        _rate = new SlidingWindowRateLimiter(_time, TimeSpan.FromMinutes(1));
    }

    /// <inheritdoc />
    public async Task<MemoryContextOutcome> RetrieveAsync(MemoryContextCaller caller, MemoryContextRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);

        // The identity is the session's. An agent reads only as itself; the owner names the subject agent.
        VaultPrincipal principal;
        switch (caller.Kind)
        {
            case VaultPrincipalKind.Agent when caller.AgentId is not null:
                if (!string.Equals(request.AgentId, caller.AgentId, StringComparison.Ordinal))
                {
                    return MemoryContextOutcome.Fail(MemoryContextErrorCodes.Unauthorized, "An agent reads only its own memory.");
                }

                principal = VaultPrincipal.Agent(caller.AgentId);
                break;
            case VaultPrincipalKind.Owner:
                principal = VaultPrincipal.Owner();
                break;
            default:
                return MemoryContextOutcome.Fail(MemoryContextErrorCodes.Unauthorized, "This caller may not read memory context.");
        }

        if (request.AgentId is null || !VaultPath.IsValidIdentifier(request.AgentId))
        {
            return MemoryContextOutcome.Fail(MemoryContextErrorCodes.BadRequest, "The agent id must be a single valid path segment.");
        }

        if (string.IsNullOrWhiteSpace(request.QueryText))
        {
            return MemoryContextOutcome.Fail(MemoryContextErrorCodes.BadRequest, "Query text is required.");
        }

        if (request.ProjectScope is not null && !VaultPath.IsValidIdentifier(request.ProjectScope))
        {
            return MemoryContextOutcome.Fail(MemoryContextErrorCodes.BadRequest, "The project scope must be a project slug.");
        }

        if (request.TokenBudget < 0 || request.MaxHits < 0)
        {
            return MemoryContextOutcome.Fail(MemoryContextErrorCodes.BadRequest, "Token budget and max hits must not be negative.");
        }

        if (request.AlreadyInContext is { } already && already.Count > _options.MaxAlreadyInContext)
        {
            return MemoryContextOutcome.Fail(MemoryContextErrorCodes.BadRequest, "Too many ids already in context.");
        }

        MemoryContextLimits limits = (await _limits.GetAsync(ct).ConfigureAwait(false)).Clamped();
        if (request.QueryText.Length > limits.MaxQueryChars)
        {
            return MemoryContextOutcome.Fail(MemoryContextErrorCodes.TooLarge, "The query text is longer than the limit.");
        }

        if (!_rate.TryAcquire(caller.Actor, limits.RatePerAgentPerMinute))
        {
            return MemoryContextOutcome.Fail(MemoryContextErrorCodes.RateLimited, "Too many memory context calls this minute.");
        }

        int budget = request.TokenBudget == 0 ? limits.MaxTokens : Math.Min(request.TokenBudget, limits.MaxTokens);
        int maxHits = request.MaxHits == 0 ? limits.MaxHits : Math.Min(request.MaxHits, limits.MaxHits);

        string[] scope = request.ProjectScope is null
            ? [$"agents/{request.AgentId}/memory/"]
            : [$"agents/{request.AgentId}/memory/", $"projects/{request.ProjectScope}/"];
        var alreadyInContext = new HashSet<string>(request.AlreadyInContext ?? [], StringComparer.Ordinal);
        var texts = new Dictionary<string, PackedText>(StringComparer.Ordinal);
        var eligible = new HashSet<string>(StringComparer.Ordinal);

        MemoryGraphRetrieval retrieval;
        try
        {
            IReadOnlyList<MemoryVectorCandidate> lexicalRows = await _reader.LexicalDocsAsync(
                request.QueryText,
                MemoryPathFilter.For(principal, scope, _vaultId),
                _options.SeedCandidates,
                ct).ConfigureAwait(false);
            RankedHit[] lexical = MemoryContextSeeds.ToRanked(lexicalRows.Where(row => CanRead(principal, row.VaultPath)));

            async Task<IReadOnlyDictionary<string, int>> CountAsync(IReadOnlyCollection<string> ids, CancellationToken token)
            {
                var docIds = ids.Select(id => Guid.TryParse(id, out Guid docId) ? docId : Guid.Empty).Where(id => id != Guid.Empty).ToArray();
                IReadOnlyList<MemoryContextDocText> rows = await _reader.ReadAsync(docIds, token).ConfigureAwait(false);
                var counts = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (MemoryContextDocText row in rows)
                {
                    string key = row.DocId.ToString("D");
                    texts[key] = Pack(row);
                    LogOwnerRead(principal, row.VaultPath);
                    if (texts[key].Excluded
                        || alreadyInContext.Contains(key)
                        || (row.NoteId is not null && alreadyInContext.Contains(row.NoteId)))
                    {
                        continue;
                    }

                    counts[key] = texts[key].Tokens;
                    eligible.Add(key);
                }

                return counts;
            }

            retrieval = await _retriever.RetrieveForQueryAsync(
                principal,
                request.QueryText,
                lexical,
                CountAsync,
                budget,
                new MemoryGraphOptions(MaxHops: _options.MaxHops, TopK: _options.TopK, IncludeHistory: request.IncludeHistory, ScopePrefixes: scope),
                _options.SeedCandidates,
                ct,
                new MemoryReadContext(caller.Actor, caller.Session, caller.Route)).ConfigureAwait(false);
        }
        catch (NpgsqlException)
        {
            return MemoryContextOutcome.Fail(MemoryContextErrorCodes.Unavailable, "The memory index is unavailable.");
        }
        catch (MemoryReadLogException)
        {
            return MemoryContextOutcome.Fail(MemoryContextErrorCodes.Unavailable, "The read could not be logged, so it was refused.");
        }
        catch (MemoryAccessLogException)
        {
            return MemoryContextOutcome.Fail(MemoryContextErrorCodes.Unavailable, "The owner read could not be logged, so it was refused.");
        }

        var packed = retrieval.Hits.Take(maxHits).ToList();
        var noteByDoc = texts.ToDictionary(pair => pair.Key, pair => pair.Value.NoteId, StringComparer.Ordinal);
        var hits = packed.Select(hit => ToDto(hit, texts[hit.DocId], noteByDoc)).ToList();
        int tokensUsed = packed.Sum(hit => texts[hit.DocId].Tokens);
        bool truncated = eligible.Count > hits.Count;
        string auditId = Guid.CreateVersion7(_time.GetUtcNow()).ToString("D");

        var payload = new JsonObject
        {
            ["call_id"] = auditId,
            ["route"] = caller.Route,
            ["session"] = caller.Session,
            ["agent_scope"] = request.AgentId,
            ["project_scope"] = request.ProjectScope,
            ["result_count"] = hits.Count,
            // "tokens_used" in the spec; the payload guard refuses any key containing "token", so the count is "budget_used".
            ["budget_used"] = tokensUsed,
            ["truncated"] = truncated,
            ["lexical_only"] = retrieval.LexicalOnly,
            ["lexical_only_reason"] = retrieval.LexicalOnlyReason,
            ["taint_tags"] = new JsonArray(hits.Any(h => h.Labelled) ? [JsonValue.Create("labelled_quoted_data")] : []),
            ["doc_ids"] = new JsonArray([.. hits.Select(h => (JsonNode?)JsonValue.Create(h.DocId))]),
            ["note_ids"] = new JsonArray([.. hits.Where(h => h.NoteId is not null).Select(h => (JsonNode?)JsonValue.Create(h.NoteId))]),
        };
        AuditPayloadGuard.Ensure(payload);
        try
        {
            await _audit.AppendAsync(new AuditEntry(_time.GetUtcNow(), caller.Actor, AuditKind, payload.ToJsonString()), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A read that could not be audited is not returned.
            return MemoryContextOutcome.Fail(MemoryContextErrorCodes.Unavailable, "The retrieval could not be audited, so it was refused.");
        }

        return MemoryContextOutcome.Ok(new MemoryContextResult(hits, retrieval.LexicalOnly, retrieval.LexicalOnlyReason, tokensUsed, truncated, auditId));
    }

    private bool CanRead(VaultPrincipal who, string path) =>
        VaultPath.TryParse(path, out VaultPath? parsed) && _access.CanRead(who, parsed!);

    /// <summary>Every owner read of another agent's private area is logged before the result is returned (doc 08 section 8).</summary>
    private void LogOwnerRead(VaultPrincipal who, string path)
    {
        if (_accessLog is null || !VaultPath.TryParse(path, out VaultPath? parsed))
        {
            return;
        }

        AccessDecision decision = _access.Explain(who, parsed!);
        if (!decision.Logged)
        {
            return;
        }

        try
        {
            _accessLog.Record(new MemoryAccessEntry(_ownerReader, parsed!.Value, decision.Reason, _time.GetUtcNow()));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new MemoryAccessLogException(ex);
        }
    }

    private PackedText Pack(MemoryContextDocText row)
    {
        MemoryTier? tier = row.Trust is not null && Enum.TryParse(row.Trust, ignoreCase: false, out MemoryTier parsed) ? parsed : null;
        bool excluded = row.NoteId is not null && (tier is null || MemoryTrustRules.RetrievalFor(tier.Value) == MemoryRetrieval.Excluded);
        bool labelled = tier is { } t && MemoryTrustRules.RetrievalFor(t) == MemoryRetrieval.LabelledQuotedData;

        string body = row.Body.Trim();
        if (body.Length > _options.ExcerptChars)
        {
            body = string.Concat(body.AsSpan(0, _options.ExcerptChars - 3), "...");
        }

        if (labelled)
        {
            var quoted = new StringBuilder();
            foreach (string line in body.Split('\n'))
            {
                if (quoted.Length > 0)
                {
                    quoted.Append('\n');
                }

                quoted.Append("> ").Append(line.TrimEnd('\r'));
            }

            body = quoted.ToString();
        }

        string? tierName = tier is { } value ? "T" + ((int)value).ToString(CultureInfo.InvariantCulture) : null;
        return new PackedText(row.NoteId, row.VaultPath, tierName, body, labelled, excluded, (body.Length + 3) / 4);
    }

    private static MemoryContextHitDto ToDto(MemoryGraphHit hit, PackedText text, Dictionary<string, string?> noteByDoc)
    {
        string? via = hit.ViaDocId is null ? null : noteByDoc.TryGetValue(hit.ViaDocId, out string? note) && note is not null ? note : hit.ViaDocId;
        return new MemoryContextHitDto(
            hit.DocId,
            hit.NoteId,
            hit.Title,
            ScopeOf(hit.VaultPath),
            text.Tier,
            hit.Kind,
            hit.ScopeAgentId is null ? "vault" : "agent:" + hit.ScopeAgentId,
            text.Excerpt,
            text.Labelled,
            hit.Score,
            hit.HopDistance,
            via,
            hit.Version,
            hit.IsHistory,
            hit.SupersededBy);
    }

    private static string ScopeOf(string path) => path switch
    {
        _ when path.StartsWith("agents/", StringComparison.Ordinal) && path.Contains("/memory/", StringComparison.Ordinal) => "private",
        _ when path.StartsWith("projects/", StringComparison.Ordinal) => "project",
        _ when path.StartsWith("company/memory/", StringComparison.Ordinal) => "company",
        _ when path.StartsWith("hive/", StringComparison.Ordinal) => "hive",
        _ => "vault",
    };

    private sealed record PackedText(string? NoteId, string Path, string? Tier, string Excerpt, bool Labelled, bool Excluded, int Tokens);

    private sealed class MemoryAccessLogException(Exception inner) : Exception("The owner access log refused the entry.", inner);
}

/// <summary>A per-key sliding window: at most N acquisitions within the window. Thread-safe.</summary>
internal sealed class SlidingWindowRateLimiter(TimeProvider time, TimeSpan window)
{
    private readonly Dictionary<string, Queue<DateTimeOffset>> _calls = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public bool TryAcquire(string key, int limit)
    {
        DateTimeOffset now = time.GetUtcNow();
        lock (_gate)
        {
            if (!_calls.TryGetValue(key, out Queue<DateTimeOffset>? calls))
            {
                _calls[key] = calls = new Queue<DateTimeOffset>();
            }

            while (calls.Count > 0 && now - calls.Peek() >= window)
            {
                calls.Dequeue();
            }

            if (calls.Count >= limit)
            {
                return false;
            }

            calls.Enqueue(now);
            return true;
        }
    }
}
