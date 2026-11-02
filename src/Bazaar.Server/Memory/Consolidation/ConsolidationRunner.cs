using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Bazaar.Contracts.Gateway;
using Bazaar.Server.Gateway;
using Bazaar.Server.Memory.Notes;
using Bazaar.Server.Memory.Trust;
using Bazaar.Server.Memory.Write;
using Bazaar.Server.Security.Taint;

namespace Bazaar.Server.Memory.Consolidation;

/// <summary>
/// The consolidation ("coffee break") job for one agent (docs/design/08-memory-system.md, section 7).
/// One run: take the agent's lock, read episodes since the last run, ask the model once through a reader
/// session (no tools offered, JSON reply only), validate each proposed note with <see cref="SoulGuard"/>, and
/// commit every accepted note in one batch. Output inherits the highest input taint: any tainted input makes
/// every output note start at <see cref="MemoryTier.UntrustedDerived"/>.
/// </summary>
/// <remarks>
/// Caps: the time cap is measured on <see cref="TimeProvider"/> after every streamed event. The token cap counts
/// the gateway's reported usage (input, cache writes, cache reads and output; thinking is part of output). When
/// the gateway reports no usage, the count is (prompt characters + streamed characters) / 4, and the report says
/// so with <see cref="ConsolidationReport.TokensEstimated"/>.
/// <para>
/// The batch sink takes only the batch, so the commit message built by <see cref="MemoryCommitMessage.Build"/> is
/// returned in the report and is not passed to the sink.
/// </para>
/// </remarks>
public sealed class ConsolidationRunner
{
    private const string _system = "memory-consolidation";
    private const string _consolidationAuthor = "consolidation";
    private const string _alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    private readonly IModelGateway _gateway;
    private readonly IEpisodeSource _episodes;
    private readonly IConsolidationLock _locks;
    private readonly IMemoryBatchSink _sink;
    private readonly TimeProvider _time;

    /// <summary>Creates a runner.</summary>
    public ConsolidationRunner(
        IModelGateway gateway,
        IEpisodeSource episodes,
        IConsolidationLock locks,
        IMemoryBatchSink sink,
        TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(episodes);
        ArgumentNullException.ThrowIfNull(locks);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(time);

        _gateway = gateway;
        _episodes = episodes;
        _locks = locks;
        _sink = sink;
        _time = time;
    }

    /// <summary>Runs consolidation once for an agent.</summary>
    public async Task<ConsolidationReport> RunAsync(string agentId, ConsolidationOptions options, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(agentId);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        using var lease = await _locks.TryAcquireAsync(agentId, ct);
        if (lease is null)
        {
            return Ended(ConsolidationStatus.SkippedLocked, 0, 0, [], "Another consolidation run is already running for this agent.", 0, false);
        }

        var start = _time.GetUtcNow();
        var all = await _episodes.ReadSinceLastRunAsync(agentId, ct);
        var inputs = all.Where(e => e.Trust != MemoryTier.Quarantined && e.SourceTrust != ContentTrust.Quarantined).ToArray();
        var quarantined = all.Count - inputs.Length;

        if (inputs.Length == 0)
        {
            return Ended(ConsolidationStatus.Completed, 0, quarantined, [], "No episodes to consolidate.", 0, false);
        }

        var inputIds = inputs.Select(e => e.Id).ToArray();
        var request = ConsolidationPrompt.Build(agentId, inputs, options);
        var promptTokens = EstimateTokens(request.Messages.Sum(m => (long)m.Text.Length));
        if (promptTokens > options.MaxTokens)
        {
            return Ended(ConsolidationStatus.CapExceeded, inputs.Length, quarantined, [], "The prompt alone exceeds the token cap.", promptTokens, true);
        }

        var call = await ReadReplyAsync(request, options, start, ct);
        if (call.Status != ConsolidationStatus.Completed)
        {
            return Ended(call.Status, inputs.Length, quarantined, [], call.Detail, call.Tokens, call.Estimated);
        }

        if (!ConsolidationReplyParser.TryParse(call.Text, out var reply, out var parseDetail) || reply is null)
        {
            return Ended(ConsolidationStatus.MalformedOutput, inputs.Length, quarantined, [], parseDetail, call.Tokens, call.Estimated);
        }

        var provenance = ProvenanceFor(inputs);
        var tier = MemoryTrustRules.TierForWrite(provenance);
        var at = _time.GetUtcNow().ToString("o", CultureInfo.InvariantCulture);
        var accepted = new List<MemoryWriteItem>();
        var rejected = new List<RejectedOperation>();
        for (var i = 0; i < reply.Operations.Count; i++)
        {
            var outcome = ValidateOperation(agentId, i, reply.Operations[i], inputIds, provenance, tier, at, options.MaxNoteBytes);
            if (outcome.Item is { } item)
            {
                accepted.Add(item);
            }
            else if (outcome.Rejection is { } rejection)
            {
                rejected.Add(rejection);
            }
        }

        var maintenance = reply.MaintenanceReport
            ?? $"Consolidation: {accepted.Count} operation(s) accepted, {rejected.Count} rejected.";

        string? commitSha = null;
        string? commitMessage = null;
        if (accepted.Count > 0)
        {
            var batch = MemoryBatch.Create(agentId, accepted);
            commitMessage = MemoryCommitMessage.Build(batch);
            var commit = await _sink.CommitAsync(batch, ct);
            if (!commit.Succeeded)
            {
                return new ConsolidationReport(
                    ConsolidationStatus.CommitFailed, inputs.Length, quarantined, 0, rejected, maintenance,
                    null, commitMessage, call.Tokens, call.Estimated, commit.FailureReason);
            }

            commitSha = commit.CommitSha;
        }

        await _episodes.MarkConsumedAsync(agentId, inputIds, ct);
        return new ConsolidationReport(
            ConsolidationStatus.Completed, inputs.Length, quarantined, accepted.Count, rejected, maintenance,
            commitSha, commitMessage, call.Tokens, call.Estimated, null);
    }

    private async Task<CallResult> ReadReplyAsync(ModelRequest request, ConsolidationOptions options, DateTimeOffset start, CancellationToken ct)
    {
        var promptChars = request.Messages.Sum(m => (long)m.Text.Length);
        var streamedChars = 0L;
        var reply = new StringBuilder();
        var sawUsage = false;
        var usageTokens = 0L;
        var sawDone = false;
        var limit = TimeSpan.FromMinutes(options.MaxMinutes);

        await using var events = _gateway.CallAsync(request, ct).GetAsyncEnumerator(ct);
        while (await events.MoveNextAsync())
        {
            switch (events.Current)
            {
                case ModelEvent.TextDelta text:
                    reply.Append(text.Text);
                    streamedChars += text.Text.Length;
                    break;

                case ModelEvent.ThinkingDelta thinking:
                    streamedChars += thinking.Text.Length;
                    break;

                case ModelEvent.ToolCallStart:
                case ModelEvent.ToolCallArgsDelta:
                case ModelEvent.ToolCallEnd:
                    return CallResult.Stopped(ConsolidationStatus.MalformedOutput, "The reader session offered no tools but the model called one.");

                case ModelEvent.UsageReported usage:
                    sawUsage = true;
                    usageTokens = Total(usage.Usage);
                    break;

                case ModelEvent.Failure failure:
                    return CallResult.Stopped(ConsolidationStatus.GatewayFailed, $"The gateway failed: {failure.Code}.");

                case ModelEvent.Done:
                    sawDone = true;
                    break;
            }

            var tokens = sawUsage ? usageTokens : EstimateTokens(promptChars + streamedChars);
            if (tokens > options.MaxTokens)
            {
                return CallResult.Stopped(ConsolidationStatus.CapExceeded, "The token cap was reached.", tokens, !sawUsage);
            }

            if (_time.GetUtcNow() - start >= limit)
            {
                return CallResult.Stopped(ConsolidationStatus.CapExceeded, "The time cap was reached.", tokens, !sawUsage);
            }
        }

        var finalTokens = sawUsage ? usageTokens : EstimateTokens(promptChars + streamedChars);
        if (!sawDone)
        {
            return CallResult.Stopped(ConsolidationStatus.GatewayFailed, "The stream ended without a done event.", finalTokens, !sawUsage);
        }

        return new CallResult(ConsolidationStatus.Completed, reply.ToString(), null, finalTokens, !sawUsage);
    }

    private static long Total(GatewayTokenUsage usage) =>
        usage.Input + usage.CacheWrite5m + usage.CacheWrite1h + usage.CacheRead + usage.Output;

    private static long EstimateTokens(long characters) => characters / 4;

    /// <summary>
    /// Output provenance: the least trusted source among the inputs (enum order is trust order, most trusted
    /// first), and taint when any input is UntrustedDerived or External.
    /// </summary>
    private static MemoryWriteInput ProvenanceFor(IReadOnlyList<EpisodeNote> inputs)
    {
        var tainted = inputs.Any(e => e.Trust == MemoryTier.UntrustedDerived || e.SourceTrust == ContentTrust.External);
        var source = inputs.Max(e => e.SourceTrust);
        return new MemoryWriteInput(
            source,
            tainted ? TaintState.Hard : TaintState.Clean,
            tainted,
            MemoryActor.System);
    }

    private static (MemoryWriteItem? Item, RejectedOperation? Rejection) ValidateOperation(
        string agentId,
        int index,
        ProposedOperation operation,
        IReadOnlyList<string> inputIds,
        MemoryWriteInput provenance,
        MemoryTier tier,
        string at,
        int maxNoteBytes)
    {
        var shape = ShapeOf(operation, at);
        if (shape is null)
        {
            return (null, new RejectedOperation(index, operation.Op, null, ["unknown operation or missing field"]));
        }

        var (kind, status, deprecation, title, body) = shape.Value;
        var folder = MemoryAreas.ExpectedDirectory(kind, MemoryScope.Private, agentId);
        if (folder is null)
        {
            return (null, new RejectedOperation(index, operation.Op, null, ["kind-not-allowed-in-area"]));
        }

        var path = folder + "/" + operation.Slug + ".md";
        var id = DerivedId(agentId, inputIds, index);
        var note = new MemoryNote(
            Id: id,
            Type: MemoryNoteTypes.TypeFor(kind),
            Title: title,
            Project: null,
            Kind: kind,
            Status: status,
            Version: 1,
            Trust: tier,
            SourceTrust: provenance.SourceTrust,
            Tainted: provenance.SessionState == TaintState.Hard,
            Origin: null,
            DerivedFrom: inputIds,
            Subjects: [],
            Deprecated: deprecation,
            Supersedes: []);

        var text = NoteDocument.Create(note, body).Serialize();
        var check = SoulGuard.Validate(text, new MemoryWriteRequest(path, provenance, null), maxNoteBytes);
        if (!check.Allowed)
        {
            return (null, new RejectedOperation(index, operation.Op, path, check.Reasons.Select(r => r.ToString()).ToArray()));
        }

        var item = new MemoryWriteItem(
            DerivedGuid(agentId, inputIds, index),
            path,
            text,
            null,
            null,
            check.Flagged ? "consolidation: flagged" : "consolidation: passed");
        return (item, null);
    }

    /// <summary>The kind, status, front matter and body an operation produces, or null when it is not valid.</summary>
    private static (MemoryKind Kind, MemoryNoteStatus Status, MemoryDeprecation? Deprecation, string Title, string Body)? ShapeOf(ProposedOperation operation, string at)
    {
        if (string.IsNullOrEmpty(operation.Slug))
        {
            return null;
        }

        switch (operation.Op)
        {
            case "create_semantic" when !string.IsNullOrEmpty(operation.Title) && operation.Body is not null:
                return (MemoryKind.Semantic, MemoryNoteStatus.Active, null, operation.Title, operation.Body);

            case "rewrite_lesson" when !string.IsNullOrEmpty(operation.Title) && operation.Body is not null:
                return (MemoryKind.Procedural, MemoryNoteStatus.Active, null, operation.Title, operation.Body);

            case "deprecate" when TargetKind(operation.TargetKind) is { } target
                && !string.IsNullOrEmpty(operation.ReplacedBy) && operation.Reason is not null:
                return (
                    target,
                    MemoryNoteStatus.Deprecated,
                    new MemoryDeprecation(operation.Reason, _consolidationAuthor, at),
                    operation.Title ?? operation.Slug,
                    "Deprecated. Replaced by [[" + operation.ReplacedBy + "]].");

            default:
                return null;
        }
    }

    private static MemoryKind? TargetKind(string? name) => name switch
    {
        "semantic" => MemoryKind.Semantic,
        "procedural" => MemoryKind.Procedural,
        "failure" => MemoryKind.Failure,
        "preference" => MemoryKind.Preference,
        _ => null,
    };

    /// <summary>A deterministic ULID-shaped id, so a fixed run gives the same ids.</summary>
    private static string DerivedId(string agentId, IReadOnlyList<string> inputIds, int index)
    {
        var digest = Digest(agentId, inputIds, index);
        var chars = new char[26];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = _alphabet[digest[i % digest.Length] % _alphabet.Length];
        }

        return new string(chars);
    }

    private static Guid DerivedGuid(string agentId, IReadOnlyList<string> inputIds, int index)
    {
        var digest = Digest(agentId, inputIds, index);
        return new Guid(digest.AsSpan(0, 16));
    }

    private static byte[] Digest(string agentId, IReadOnlyList<string> inputIds, int index) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', _system, agentId, string.Join(',', inputIds), index.ToString(System.Globalization.CultureInfo.InvariantCulture))));

    private static ConsolidationReport Ended(
        ConsolidationStatus status,
        int episodes,
        int quarantined,
        IReadOnlyList<RejectedOperation> rejected,
        string? detail,
        long tokens,
        bool estimated) =>
        new(status, episodes, quarantined, 0, rejected, string.Empty, null, null, tokens, estimated, detail);

    private readonly record struct CallResult(
        ConsolidationStatus Status,
        string Text,
        string? Detail,
        long Tokens,
        bool Estimated)
    {
        public static CallResult Stopped(ConsolidationStatus status, string detail, long tokens = 0, bool estimated = false) =>
            new(status, string.Empty, detail, tokens, estimated);
    }
}
