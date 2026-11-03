using Bazaar.Contracts.Gc;
using Bazaar.Contracts.Soul;
using Bazaar.Server.Gc.Write;
using Bazaar.Server.Souls.Judgments;

namespace Bazaar.Server.Tests.Souls.Judgments;

/// <summary>In-memory decision file writer. Idempotent by decision id, like the port requires. Records the call order.</summary>
internal sealed class FakeDecisionFileWriter(List<string>? trace = null) : IDecisionFileWriter
{
    private readonly Dictionary<Guid, DecisionFileReceipt> _byId = [];
    private int _next = 1;

    public List<DecisionFile> Writes { get; } = [];

    public Exception? FailWith { get; set; }

    public Task<DecisionFileReceipt> WriteAsync(DecisionFile file, CancellationToken cancellationToken)
    {
        trace?.Add("file");
        if (FailWith is not null)
            throw FailWith;
        if (_byId.TryGetValue(file.DecisionId, out var existing))
            return Task.FromResult(existing);
        var number = _next++;
        var receipt = new DecisionFileReceipt($"decisions/{number:D4}-{file.Slug}.md", number);
        _byId[file.DecisionId] = receipt;
        Writes.Add(file);
        return Task.FromResult(receipt);
    }
}

/// <summary>In-memory judgment poster. Idempotent by decision id, like the port requires. Records the call order.</summary>
internal sealed class FakeGcJudgmentPoster(List<string>? trace = null) : IGcJudgmentPoster
{
    private readonly Dictionary<Guid, long> _seqByDecision = [];
    private long _nextSeq = 10;

    public List<JudgmentPost> Posts { get; } = [];

    public Exception? FailWith { get; set; }

    public Task<JudgmentPostReceipt> PostAsync(JudgmentPost post, CancellationToken cancellationToken)
    {
        trace?.Add("post");
        if (FailWith is not null)
            throw FailWith;
        if (_seqByDecision.TryGetValue(post.DecisionId, out var seq))
            return Task.FromResult(new JudgmentPostReceipt(seq, true));
        var assigned = _nextSeq++;
        _seqByDecision[post.DecisionId] = assigned;
        Posts.Add(post);
        return Task.FromResult(new JudgmentPostReceipt(assigned, false));
    }
}

/// <summary>
/// Fake GC log writer for the poster adapter. Records each request, assigns sequential seqs, and answers a repeat
/// <c>(author, ClientMsgId)</c> with the original event, as the real writer does.
/// </summary>
internal sealed class FakeGcLogWriter : IGcLogWriter
{
    private readonly Dictionary<(Guid Author, string ClientMsgId), AppendResult> _seen = [];
    private long _seq;

    public List<AppendRequest> Requests { get; } = [];

    public Task<AppendResult> AppendAsync(AppendRequest request, CancellationToken ct)
    {
        Requests.Add(request);
        var key = (request.AuthorId, request.ClientMsgId);
        if (_seen.TryGetValue(key, out var original))
            return Task.FromResult(original with { Duplicate = true });
        var result = new AppendResult(request.GcId, ++_seq, Guid.NewGuid(), false, DateTimeOffset.UnixEpoch);
        _seen[key] = result;
        return Task.FromResult(result);
    }

    public async Task<IReadOnlyList<AppendResult>> AppendManyAsync(IReadOnlyList<AppendRequest> requests, CancellationToken ct)
    {
        var results = new List<AppendResult>();
        foreach (var request in requests)
            results.Add(await AppendAsync(request, ct));
        return results;
    }
}

/// <summary>Builders for valid judgment records.</summary>
internal static class JudgmentFixtures
{
    public static readonly Guid SoulId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid GcId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid AgentId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    public static DecisionRecord Record(Guid? decisionId = null, string title = "Promote Ada to reviewer") => new(
        decisionId ?? Guid.Parse("44444444-4444-4444-4444-444444444444"),
        SoulId,
        7,
        GcId,
        AgentId,
        JudgmentKind.Promotion,
        title,
        [new JudgmentInput(1042, SourceTrust.External), new JudgmentInput(1050, SourceTrust.Owner)],
        ["promote", "hold"],
        "promote",
        "Ada reviewed eight PRs with no reverts and the owner asked for more review load.",
        JudgmentConfidence.Medium,
        new DateTimeOffset(2026, 10, 30, 9, 0, 0, TimeSpan.Zero));
}
