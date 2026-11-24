using System.Globalization;
using Bazaar.Contracts.Memory;
using Bazaar.Server.Memory.Access;
using Bazaar.Server.Memory.Notes;
using Bazaar.Server.Memory.Retrieval;
using Bazaar.Server.Memory.Trust;
using Bazaar.Server.Memory.Write;
using Bazaar.Server.Security.Taint;

namespace Bazaar.Server.Memory.Tools;

/// <summary>
/// The four memory tools (docs/design/08-memory-system.md, section 2): store, retrieve, update and deprecate. Each
/// call takes the calling <see cref="VaultPrincipal"/> and returns the wire DTO, or a refusal. There is no hard delete.
/// <para>
/// Writes are queued per agent (<see cref="MemoryWriteQueueRegistry"/>) and reach the Vault on the next flush. Until
/// then, a pending overlay holds the newest text of each touched note, so a second update on the same version
/// conflicts and reads see the agent's own writes. The overlay is dropped when the agent's queue is empty after
/// <see cref="FlushTurnAsync"/>.
/// </para>
/// </summary>
public sealed class MemoryToolService : IDisposable
{
    private static readonly ContentTrust[] _ownerOrigins = [ContentTrust.Owner, ContentTrust.Agent, ContentTrust.External];
    private static readonly ContentTrust[] _agentOrigins = [ContentTrust.Agent, ContentTrust.External];
    private static readonly ContentTrust[] _maintenanceOrigins = [ContentTrust.System, ContentTrust.Agent, ContentTrust.External];

    private readonly IMemoryVault _vault;
    private readonly MemoryWriteQueueRegistry _queues;
    private readonly IVaultAccess _access;
    private readonly IMemoryAccessLog _accessLog;
    private readonly TimeProvider _time;
    private readonly MemoryToolOptions _options;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly object _sync = new();
    private readonly Dictionary<string, PendingNote> _pending = new(StringComparer.Ordinal);

    /// <summary>Creates the service.</summary>
    /// <param name="vault">The Vault seam: reads, lexical search and the batch sink.</param>
    /// <param name="queues">Per-agent write queues.</param>
    /// <param name="access">The Vault access layer. Every read goes through it.</param>
    /// <param name="accessLog">Receives owner reads of private memory.</param>
    /// <param name="time">Clock for timestamps and ids.</param>
    /// <param name="options">Install settings; defaults when null.</param>
    public MemoryToolService(
        IMemoryVault vault,
        MemoryWriteQueueRegistry queues,
        IVaultAccess access,
        IMemoryAccessLog accessLog,
        TimeProvider time,
        MemoryToolOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(vault);
        ArgumentNullException.ThrowIfNull(queues);
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(accessLog);
        ArgumentNullException.ThrowIfNull(time);

        _options = options ?? new MemoryToolOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaxNoteBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.QueryCandidates, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.RrfK, 1);

        _vault = vault;
        _queues = queues;
        _access = access;
        _accessLog = accessLog;
        _time = time;
    }

    /// <summary>
    /// Creates a note in the caller's private area, a project area or the company area, with the origin's trust.
    /// <paramref name="sessionState"/> is the taint of the session, supplied by the runtime and never by the agent.
    /// </summary>
    public async Task<MemoryToolOutcome<MemoryStoreResult>> StoreAsync(
        VaultPrincipal principal,
        MemoryStoreRequest request,
        TaintState sessionState = TaintState.Clean,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(request);

        var (caller, refusal) = ResolveCaller(principal, request.AgentId);
        if (caller is null)
        {
            return Refuse<MemoryStoreResult>(refusal!);
        }

        if (!MemoryNoteWire.TryParseKind(request.Kind, out var kind) || kind == MemoryKind.Hive)
        {
            return Fail<MemoryStoreResult>(MemoryToolErrorCode.Invalid, "Kind must be an agent memory kind.");
        }

        if (!MemoryToolMapping.TryParseScope(request.Scope, out var scope) || scope == MemoryScope.Hive)
        {
            return Fail<MemoryStoreResult>(MemoryToolErrorCode.Invalid, "Scope must be private, project or company; hive copies come only from the copy flow.");
        }

        if (!MemoryNoteWire.TryParseSourceTrust(request.Origin, out var origin) || !caller.Origins.Contains(origin))
        {
            return Fail<MemoryStoreResult>(MemoryToolErrorCode.Forbidden, "Origin is not allowed for this caller.");
        }

        if (string.IsNullOrWhiteSpace(request.Title) || string.IsNullOrWhiteSpace(request.Body))
        {
            return Fail<MemoryStoreResult>(MemoryToolErrorCode.Invalid, "Title and body are required.");
        }

        string? project = null;
        string? owner = null;
        switch (scope)
        {
            case MemoryScope.Private:
                owner = request.AgentId;
                break;
            case MemoryScope.Project:
                if (request.Project is null || !VaultPath.IsValidIdentifier(request.Project))
                {
                    return Fail<MemoryStoreResult>(MemoryToolErrorCode.Invalid, "A project scope needs a valid project slug.");
                }

                project = request.Project;
                owner = request.Project;
                break;
        }

        var now = _time.GetUtcNow();
        var id = MemoryNoteIds.NewUlid(now);
        var path = MemoryToolMapping.NotePath(scope, kind, owner ?? string.Empty, id, request.Title, DateOnly.FromDateTime(now.UtcDateTime));
        if (!VaultPath.TryParse(path, out var vaultPath))
        {
            return Fail<MemoryStoreResult>(MemoryToolErrorCode.Invalid, "The note path is not valid.");
        }

        var provenance = new MemoryWriteInput(origin, sessionState, InheritsExternal: false, caller.Actor);
        var tier = MemoryTrustRules.TierForWrite(provenance);
        var messages = request.TurnId is null ? new List<string>() : new List<string> { request.TurnId };
        var note = new MemoryNote(
            Id: id,
            Type: MemoryNoteTypes.TypeFor(kind),
            Title: request.Title.Trim(),
            Project: project,
            Kind: kind,
            Status: MemoryNoteStatus.Active,
            Version: 1,
            Trust: tier,
            SourceTrust: origin,
            Tainted: sessionState != TaintState.Clean || origin == ContentTrust.External,
            Origin: new MemoryOrigin(null, messages),
            DerivedFrom: [],
            Subjects: [],
            Deprecated: null,
            Supersedes: []);

        var text = NoteDocument.Create(note, request.Body).Serialize();
        var target = vaultPath!;
        var verdict = SoulGuard.Validate(text, new MemoryWriteRequest(target.Value, provenance, caller.ActorAgent), _options.MaxNoteBytes);
        if (!verdict.Allowed)
        {
            return Fail<MemoryStoreResult>(MemoryToolErrorCode.Rejected, Describe(verdict));
        }

        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var queue = _queues.For(request.AgentId);
            await queue.EnqueueAsync(
                new MemoryWriteItem(Guid.NewGuid(), target.Value, text, null, null, verdict.Flagged ? "flagged" : "passed"),
                ct).ConfigureAwait(false);
            Remember(request.AgentId, id, target, text);
        }
        finally
        {
            _writeGate.Release();
        }

        return MemoryToolOutcome<MemoryStoreResult>.Ok(new MemoryStoreResult(
            NoteId: id,
            Path: target.Value,
            Version: 1,
            Tier: MemoryNoteWire.TierName(tier),
            Flagged: verdict.Flagged));
    }

    /// <summary>
    /// Ranked, access-filtered search over one scope. Private scope needs the caller's own agent id; project scope
    /// covers every project the caller may read; deprecated, quarantined and wrongly typed notes are never returned.
    /// Owner reads of another agent's private notes are logged for every such candidate, returned or not.
    /// </summary>
    public async Task<MemoryToolOutcome<MemoryRetrieveResult>> RetrieveAsync(
        VaultPrincipal principal,
        MemoryRetrieveRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(request);

        var (caller, refusal) = ResolveCaller(principal, request.AgentId);
        if (caller is null)
        {
            return Refuse<MemoryRetrieveResult>(refusal!);
        }

        if (string.IsNullOrWhiteSpace(request.QueryText))
        {
            return Fail<MemoryRetrieveResult>(MemoryToolErrorCode.Invalid, "Query text is required.");
        }

        if (!MemoryToolMapping.TryParseScope(request.Scope, out var scope))
        {
            return Fail<MemoryRetrieveResult>(MemoryToolErrorCode.Invalid, "Scope must be private, project, company or hive.");
        }

        if (!MemoryToolMapping.TryParseKinds(request.Kinds, out var kinds))
        {
            return Fail<MemoryRetrieveResult>(MemoryToolErrorCode.Invalid, "A kind filter names an unknown kind.");
        }

        if (request.TokenBudget < 0 || request.MaxHits < 0)
        {
            return Fail<MemoryRetrieveResult>(MemoryToolErrorCode.Invalid, "Token budget and max hits must not be negative.");
        }

        var prefix = scope switch
        {
            MemoryScope.Private => "agents/" + request.AgentId + "/memory/",
            MemoryScope.Project => "projects/",
            MemoryScope.Company => "company/memory/",
            _ => "hive/",
        };

        var candidates = await _vault.SearchLexicalAsync(request.QueryText, prefix, _options.QueryCandidates, ct).ConfigureAwait(false);
        var ranked = new List<RankedHit>();
        var byPath = new Dictionary<string, RankedHit>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            if (byPath.TryAdd(candidate.Id, candidate))
            {
                ranked.Add(candidate);
            }
        }

        foreach (var pendingPath in PendingPaths(prefix))
        {
            var hit = new RankedHit(pendingPath, 0);
            if (byPath.TryAdd(pendingPath, hit))
            {
                ranked.Add(hit);
            }
        }

        var paths = new List<VaultPath>();
        foreach (var hit in ranked)
        {
            if (VaultPath.TryParse(hit.Id, out var path))
            {
                paths.Add(path!);
            }
        }

        var visible = _access.FilterVisible(caller.Principal, paths);
        var now = _time.GetUtcNow();
        foreach (var path in visible)
        {
            var decision = _access.Explain(caller.Principal, path);
            if (decision.Logged)
            {
                _accessLog.Record(new MemoryAccessEntry(caller.Label, path.Value, decision.Reason, now));
            }
        }

        var eligible = new List<RankedHit>();
        var notes = new Dictionary<string, RetrievedNote>(StringComparer.Ordinal);
        foreach (var path in visible)
        {
            var text = await ReadCurrentAsync(path, ct).ConfigureAwait(false);
            if (text is null)
            {
                continue;
            }

            var parsed = NoteDocument.Parse(text);
            if (!parsed.IsSuccess)
            {
                continue;
            }

            var document = parsed.Document!;
            var note = document.Note;
            if (note.Status != MemoryNoteStatus.Active
                || MemoryTrustRules.RetrievalFor(note.Trust) == MemoryRetrieval.Excluded
                || (kinds is not null && !kinds.Contains(note.Kind)))
            {
                continue;
            }

            var check = MemoryAreas.CheckPath(path.Value, note.Kind);
            if (!check.IsValid || check.Scope != scope || (scope == MemoryScope.Private && check.Owner != request.AgentId))
            {
                continue;
            }

            var (excerpt, labelled) = MemoryToolMapping.Excerpt(note.Trust, document.Body);
            notes[path.Value] = new RetrievedNote(note, excerpt, labelled, check.Scope!.Value);
            eligible.Add(byPath[path.Value]);
        }

        var fused = RrfFusion.Fuse(eligible, [], new RetrievalOptions(request.TokenBudget, request.MaxHits, _options.RrfK));
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var hit in fused)
        {
            counts[hit.Id] = MemoryToolMapping.EstimateTokens(notes[hit.Id].Excerpt);
        }

        var packed = TokenBudgetPacker.Pack(fused, counts, request.TokenBudget);
        var hits = packed
            .Select(hit =>
            {
                var retrieved = notes[hit.Id];
                return MemoryToolMapping.ToHit(retrieved.Note, retrieved.Scope, retrieved.Excerpt, retrieved.Labelled);
            })
            .ToList();
        var used = packed.Sum(hit => counts[hit.Id]);

        // Lexical only: no embedding provider exists in this package, and doc 08 requires the response to say so.
        return MemoryToolOutcome<MemoryRetrieveResult>.Ok(new MemoryRetrieveResult(hits, LexicalOnly: true, TokensUsed: used));
    }

    /// <summary>
    /// Rewrites a note's body or title. A stale <paramref name="MemoryUpdateRequest.ExpectedVersion"/> is a conflict
    /// result, not an error. The trust tier never rises through an update, and SoulGuard checks the new text.
    /// </summary>
    public async Task<MemoryToolOutcome<MemoryUpdateResult>> UpdateAsync(
        VaultPrincipal principal,
        MemoryUpdateRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(request);

        var (caller, refusal) = ResolveCaller(principal, request.AgentId);
        if (caller is null)
        {
            return Refuse<MemoryUpdateResult>(refusal!);
        }

        if (request.Body is null && (request.FieldChanges is null || request.FieldChanges.Count == 0))
        {
            return Fail<MemoryUpdateResult>(MemoryToolErrorCode.Invalid, "Nothing to change.");
        }

        if (request.Body is not null && string.IsNullOrWhiteSpace(request.Body))
        {
            return Fail<MemoryUpdateResult>(MemoryToolErrorCode.Invalid, "Body must not be blank.");
        }

        if (request.FieldChanges is not null && request.FieldChanges.Any(change => change.Field != "title"))
        {
            return Fail<MemoryUpdateResult>(MemoryToolErrorCode.Invalid, "Only title can be changed by update; status changes go through deprecate.");
        }

        var newTitle = request.FieldChanges?.LastOrDefault(change => change.Field == "title")?.Value;
        if (newTitle is not null && string.IsNullOrWhiteSpace(newTitle))
        {
            return Fail<MemoryUpdateResult>(MemoryToolErrorCode.Invalid, "Title must not be blank.");
        }

        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var found = await FindCurrentAsync(caller, request.NoteId, ct).ConfigureAwait(false);
            if (found is null)
            {
                return Fail<MemoryUpdateResult>(MemoryToolErrorCode.NotFound, "No readable note has that id.");
            }

            var (path, document) = found.Value;
            var current = document.Note;
            if (current.Trust == MemoryTier.Quarantined)
            {
                return Fail<MemoryUpdateResult>(MemoryToolErrorCode.Forbidden, "Quarantined notes cannot be changed.");
            }

            if (current.Status == MemoryNoteStatus.Deprecated)
            {
                return Fail<MemoryUpdateResult>(MemoryToolErrorCode.Invalid, "Deprecated notes are read-only.");
            }

            if (current.Version != request.ExpectedVersion)
            {
                return MemoryToolOutcome<MemoryUpdateResult>.Ok(new MemoryUpdateResult(request.NoteId, Updated: false, Version: current.Version, Conflict: true));
            }

            var next = current with
            {
                Version = current.Version + 1,
                Title = newTitle?.Trim() ?? current.Title,
            };

            var error = await CommitRevisionAsync(caller, request.AgentId, path, document, next, request.Body ?? document.Body, ct).ConfigureAwait(false);
            if (error is not null)
            {
                return Refuse<MemoryUpdateResult>(error);
            }

            return MemoryToolOutcome<MemoryUpdateResult>.Ok(new MemoryUpdateResult(request.NoteId, Updated: true, Version: next.Version, Conflict: false));
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Marks a note deprecated with a reason. The file stays on disk and drops out of retrieval. Deprecating an
    /// already deprecated note succeeds without a write.
    /// </summary>
    public async Task<MemoryToolOutcome<MemoryDeprecateResult>> DeprecateAsync(
        VaultPrincipal principal,
        MemoryDeprecateRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(request);

        var (caller, refusal) = ResolveCaller(principal, request.AgentId);
        if (caller is null)
        {
            return Refuse<MemoryDeprecateResult>(refusal!);
        }

        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return Fail<MemoryDeprecateResult>(MemoryToolErrorCode.Invalid, "A deprecation reason is required.");
        }

        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var found = await FindCurrentAsync(caller, request.NoteId, ct).ConfigureAwait(false);
            if (found is null)
            {
                return Fail<MemoryDeprecateResult>(MemoryToolErrorCode.NotFound, "No readable note has that id.");
            }

            var (path, document) = found.Value;
            var current = document.Note;
            if (current.Status == MemoryNoteStatus.Deprecated)
            {
                return MemoryToolOutcome<MemoryDeprecateResult>.Ok(new MemoryDeprecateResult(request.NoteId, current.Version, Deprecated: true));
            }

            if (current.Trust == MemoryTier.Quarantined)
            {
                return Fail<MemoryDeprecateResult>(MemoryToolErrorCode.Forbidden, "Quarantined notes cannot be changed.");
            }

            var next = current with
            {
                Version = current.Version + 1,
                Status = MemoryNoteStatus.Deprecated,
                Deprecated = new MemoryDeprecation(
                    request.Reason.Trim(),
                    caller.Label,
                    _time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture)),
            };

            var error = await CommitRevisionAsync(caller, request.AgentId, path, document, next, document.Body, ct).ConfigureAwait(false);
            if (error is not null)
            {
                return Refuse<MemoryDeprecateResult>(error);
            }

            return MemoryToolOutcome<MemoryDeprecateResult>.Ok(new MemoryDeprecateResult(request.NoteId, next.Version, Deprecated: true));
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// The turn-end flush for one agent: commits its queued writes as one commit. The overlay for that agent is
    /// dropped once its queue is empty, because the Vault now holds the same text.
    /// </summary>
    public async Task FlushTurnAsync(string agentId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(agentId) || !VaultPath.IsValidIdentifier(agentId))
        {
            throw new ArgumentException("AgentId must be a single valid identifier.", nameof(agentId));
        }

        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var queue = _queues.For(agentId);
            await queue.FlushTurnAsync(ct).ConfigureAwait(false);
            if (queue.PendingCount == 0)
            {
                lock (_sync)
                {
                    foreach (var entry in _pending.Where(p => p.Value.QueueAgentId == agentId).Select(p => p.Key).ToList())
                    {
                        _pending.Remove(entry);
                    }
                }
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task<MemoryToolError?> CommitRevisionAsync(
        Caller caller,
        string queueAgentId,
        VaultPath path,
        NoteDocument current,
        MemoryNote next,
        string body,
        CancellationToken ct)
    {
        var existing = current.Note;
        var source = MemoryToolMapping.Worse(existing.SourceTrust, caller.Source);
        var provenance = new MemoryWriteInput(
            source,
            existing.Tainted ? TaintState.Hard : TaintState.Clean,
            InheritsExternal: false,
            caller.Actor);

        // The tier only ever falls on an update: an edit never promotes a note.
        var computed = MemoryTrustRules.TierForWrite(provenance);
        var tier = existing.Trust < computed ? existing.Trust : computed;
        var revised = next with { Trust = tier, SourceTrust = source };

        var text = current.WithNote(revised).WithBody(body).Serialize();
        var verdict = SoulGuard.Validate(text, new MemoryWriteRequest(path.Value, provenance, caller.ActorAgent), _options.MaxNoteBytes);
        if (!verdict.Allowed)
        {
            return new MemoryToolError(MemoryToolErrorCode.Rejected, Describe(verdict));
        }

        var queue = _queues.For(queueAgentId);
        await queue.EnqueueAsync(
            new MemoryWriteItem(Guid.NewGuid(), path.Value, text, null, null, verdict.Flagged ? "flagged" : "passed"),
            ct).ConfigureAwait(false);
        Remember(queueAgentId, revised.Id, path, text);
        return null;
    }

    private async Task<(VaultPath Path, NoteDocument Document)?> FindCurrentAsync(Caller caller, string noteId, CancellationToken ct)
    {
        VaultPath? path = null;
        lock (_sync)
        {
            path = _pending.Values.FirstOrDefault(p => p.NoteId == noteId)?.Path;
        }

        path ??= await _vault.FindNoteByIdAsync(noteId, ct).ConfigureAwait(false);

        // A hidden note looks exactly like a missing one.
        if (path is null || !_access.CanRead(caller.Principal, path))
        {
            return null;
        }

        var text = await ReadCurrentAsync(path, ct).ConfigureAwait(false);
        if (text is null)
        {
            return null;
        }

        var parsed = NoteDocument.Parse(text);
        return parsed.IsSuccess ? (path, parsed.Document!) : null;
    }

    private async Task<string?> ReadCurrentAsync(VaultPath path, CancellationToken ct)
    {
        lock (_sync)
        {
            if (_pending.TryGetValue(path.Value, out var pending))
            {
                return pending.Text;
            }
        }

        return await _vault.ReadNoteAsync(path, ct).ConfigureAwait(false);
    }

    private List<string> PendingPaths(string prefix)
    {
        lock (_sync)
        {
            return _pending.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        }
    }

    private void Remember(string queueAgentId, string noteId, VaultPath path, string text)
    {
        lock (_sync)
        {
            _pending[path.Value] = new PendingNote(queueAgentId, noteId, path, text);
        }
    }

    /// <summary>Releases the write gate. The queues are owned by the registry, not by this service.</summary>
    public void Dispose() => _writeGate.Dispose();

    private static (Caller? Caller, MemoryToolError? Refusal) ResolveCaller(VaultPrincipal principal, string agentId)
    {
        if (string.IsNullOrEmpty(agentId) || !VaultPath.IsValidIdentifier(agentId))
        {
            return (null, new MemoryToolError(MemoryToolErrorCode.Invalid, "AgentId must be a single valid identifier."));
        }

        switch (principal.Kind)
        {
            case VaultPrincipalKind.Owner:
                return (new Caller(principal, MemoryActor.Owner, ContentTrust.Owner, null, "owner", _ownerOrigins), null);

            case VaultPrincipalKind.Maintenance:
                return (new Caller(principal, MemoryActor.System, ContentTrust.System, null, "maintenance", _maintenanceOrigins), null);

            case VaultPrincipalKind.Agent:
            case VaultPrincipalKind.Cover:
                if (agentId != principal.AgentId)
                {
                    return (null, new MemoryToolError(MemoryToolErrorCode.Forbidden, "An agent may only use memory tools for itself."));
                }

                return (new Caller(principal, MemoryActor.Agent, ContentTrust.Agent, principal.AgentId, principal.AgentId!, _agentOrigins), null);

            default:
                return (null, new MemoryToolError(MemoryToolErrorCode.Forbidden, "Memory tools are for agents, the owner and maintenance."));
        }
    }

    private static string Describe(SoulGuardResult verdict) =>
        "Rejected by SoulGuard: " + string.Join(", ", verdict.Reasons);

    private static MemoryToolOutcome<T> Fail<T>(MemoryToolErrorCode code, string message)
        where T : class => MemoryToolOutcome<T>.Fail(code, message);

    private static MemoryToolOutcome<T> Refuse<T>(MemoryToolError error)
        where T : class => MemoryToolOutcome<T>.Fail(error.Code, error.Message);

    private sealed record PendingNote(string QueueAgentId, string NoteId, VaultPath Path, string Text);

    private sealed record RetrievedNote(MemoryNote Note, string Excerpt, bool Labelled, MemoryScope Scope);

    /// <summary>The resolved identity of a memory tool call: what the write is attributed to.</summary>
    private sealed record Caller(
        VaultPrincipal Principal,
        MemoryActor Actor,
        ContentTrust Source,
        string? ActorAgent,
        string Label,
        IReadOnlyList<ContentTrust> Origins);
}
