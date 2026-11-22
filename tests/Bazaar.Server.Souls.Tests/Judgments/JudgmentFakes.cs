using Bazaar.Contracts.Soul;
using Bazaar.Server.Souls.Judgments;

namespace Bazaar.Server.Souls.Tests.Judgments;

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
