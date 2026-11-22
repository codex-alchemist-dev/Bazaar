using Bazaar.Approvals;
using Bazaar.Server.Git.Merge;
using Bazaar.Server.Security.Audit;

namespace Bazaar.Server.Git.Tests;

internal sealed class FakeForge(string id = "fake") : IForgeProvider
{
    public string ProviderId { get; } = id;

    public string Head { get; set; } = TestData.Sha;

    public List<CheckRun> Checks { get; set; } = [new CheckRun("ci", CheckState.Success, TestData.Sha, null)];

    public Func<ListPrsQuery, PagedResult<PrSummary>>? OnList { get; set; }

    public List<PrFile> Files { get; set; } = [];

    public int ListCalls { get; private set; }

    public Task<PrSummary> OpenDraftPrAsync(OpenPrRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<PagedResult<PrSummary>> ListPrsAsync(RepoRef repo, ListPrsQuery query, CancellationToken cancellationToken)
    {
        ListCalls++;
        LastQuery = query;
        return Task.FromResult((OnList ?? (_ => new PagedResult<PrSummary>([TestData.Summary(1)], false)))(query));
    }

    public ListPrsQuery? LastQuery { get; private set; }

    public Task<PrDetail> GetPrAsync(PrRef pr, CancellationToken cancellationToken) =>
        Task.FromResult(new PrDetail(TestData.Summary(pr.Number), "body", 2, 3, 4, [new ReviewSummary("rev", ReviewEvent.RequestChanges, TestData.Sha, DateTimeOffset.FromUnixTimeSeconds(5))]));

    public Task<IReadOnlyList<PrFile>> GetPrFilesAsync(PrRef pr, string headSha, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<PrFile>>(Files);

    public Task<IReadOnlyList<CheckRun>> GetChecksAsync(PrRef pr, string headSha, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CheckRun>>(Checks);

    public Task<string> GetHeadShaAsync(PrRef pr, CancellationToken cancellationToken) => Task.FromResult(Head);

    public Task PostReviewAsync(PrRef pr, ReviewRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
}

internal sealed class FakeExecutor(string id = "fake") : IForgeMergeExecutor
{
    public string ProviderId { get; } = id;

    public int Calls { get; private set; }

    public Task SquashMergeAsync(PrRef pr, MergeRequest request, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.CompletedTask;
    }
}

internal sealed class FakeVerifier : IOwnerSignatureVerifier
{
    public OwnerSignatureVerdict Verdict { get; set; } = OwnerSignatureVerdict.Valid;

    public Task<OwnerSignatureVerdict> VerifyAsync(OwnerPayload payload, byte[] signature, DateTimeOffset now, CancellationToken cancellationToken) => Task.FromResult(Verdict);
}

internal sealed class FakeAudit : IAuditLog
{
    public List<AuditEntry> Entries { get; } = [];

    public bool Throws { get; set; }

    public Task AppendAsync(AuditEntry e, CancellationToken ct = default)
    {
        if (Throws)
            throw new InvalidOperationException("audit sink down");
        Entries.Add(e);
        return Task.CompletedTask;
    }
}

internal static class TestData
{
    public const string Sha = "0123456789abcdef0123456789abcdef01234567";
    public const string OtherSha = "fedcba9876543210fedcba9876543210fedcba98";

    public static readonly PrRef Pr = new("fake", "acme/widgets", 7);

    public static PrSummary Summary(long n) =>
        new(new PrRef("fake", "acme/widgets", n), "title", "bob", PrState.Open, false, Sha, new BranchName("feat"), new BranchName("main"), DateTimeOffset.FromUnixTimeSeconds(10));

    public static MergeRequest Merge(string sha = Sha) => new(sha, "Title (#7)", "Signed-off-by: Owner <o@example.test>");

    public static OwnerMergeApproval Approval(
        MergeRequest? merge = null,
        string? headSha = Sha,
        string action = GitMergeArgs.Action,
        byte[]? argsHash = null)
    {
        merge ??= Merge();
        var payload = new OwnerPayload(
            action,
            Guid.NewGuid(),
            argsHash ?? GitMergeArgs.Hash(Pr, merge),
            headSha,
            Guid.NewGuid(),
            "device-1",
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddMinutes(5),
            new byte[OwnerSignedPayload.NonceLength]);
        return new OwnerMergeApproval(Pr, payload, [1, 2, 3]);
    }
}
