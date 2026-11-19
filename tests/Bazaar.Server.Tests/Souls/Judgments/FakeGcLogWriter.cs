using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc.Write;

namespace Bazaar.Server.Tests.Souls.Judgments;

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