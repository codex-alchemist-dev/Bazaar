using System.Net;
using System.Security.Cryptography;
using Bazaar.Approvals;
using Bazaar.Contracts.GitHub;
using Bazaar.Contracts.Wire;
using Bazaar.Contracts.Wire.Codec;
using Bazaar.GitHub.Adapter;
using Bazaar.GitHub.Wire;
using Bazaar.Server.Wire.Connection;

namespace Bazaar.GitHub.Tests.Wire;

/// <summary>The client PR wire handlers (doc 21) over fake adapter, read model and merge executor, with the real owner verifier.</summary>
public sealed class PrWireHandlerTests
{
    private const string _prId = "PR_kwDO42";
    private const string _head = "a1b2c3d4e5f60718293a4b5c6d7e8f9012345678";
    private const string _moved = "ffffffffffffffffffffffffffffffffffffffff";
    private const string _ownerDeviceId = "device-1";

    private static readonly Guid _ownerHuman = Guid.Parse("0196a6f0-0000-7000-8000-000000000601");
    private static readonly PrRef _pr = new(77, "codex-alchemist-dev", "bazaar", 42);
    private static readonly JsonWireCodec _codec = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string _patch = "@@ -1,2 +1,3 @@\n a\n-b\n+c\n+d\n@@ -10 +11 @@\n-x\n+y";

    // ---- doubles ----

    private sealed class FakeConnection(string kind) : IWireConnection
    {
        public Guid ConnectionId { get; } = Guid.CreateVersion7();
        public PrincipalRef Principal { get; } = new(Guid.CreateVersion7(), kind);
        public string DeviceId => "test";
        public string DeviceClass => "test";
        public IWireCodec Codec => _codec;
        public IReadOnlyList<StreamCursor> HelloCursors => [];
        public Bazaar.Server.Wire.Sync.SubscriptionSession? Subscriptions => null;
        public CancellationToken Closed => CancellationToken.None;
        public bool TryEnqueueEvent(Envelope envelope) => true;
        public bool TryEnqueueLagged(Lagged lagged) => true;
        public ValueTask CloseAsync(int closeCode, string reason) => ValueTask.CompletedTask;
    }

    private sealed class FakeAdapter : IGitHubAdapter, IGitHubMergeExecutor
    {
        public string Head { get; set; } = _head;
        public List<ReviewRequest> Reviews { get; } = [];
        public List<MergeRequest> Merges { get; } = [];
        public Exception? MergeFailure { get; set; }
        public int FileCalls { get; private set; }

        public Task<PullRequestRef> OpenDraftPrAsync(OpenPrRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<PrFile>> GetPrFilesAsync(PrRef pr, string headSha, CancellationToken cancellationToken)
        {
            FileCalls++;
            return Task.FromResult<IReadOnlyList<PrFile>>([new PrFile("src/a.cs", "modified", 3, 2, _patch)]);
        }

        public Task PostReviewAsync(PrRef pr, ReviewRequest review, CancellationToken cancellationToken)
        {
            Reviews.Add(review);
            return Task.CompletedTask;
        }

        public Task<string> GetHeadShaAsync(PrRef pr, CancellationToken cancellationToken) => Task.FromResult(Head);

        public Func<PrChecksRollup> ChecksSource { get; set; } = () => PrChecksRollup.Passing;

        public Task<PrChecksState> GetChecksStateAsync(PrRef pr, string headSha, CancellationToken cancellationToken) =>
            Task.FromResult(ChecksSource() switch
            {
                PrChecksRollup.Passing => PrChecksState.Passing,
                PrChecksRollup.Failing => PrChecksState.Failing,
                PrChecksRollup.Pending => PrChecksState.Pending,
                _ => PrChecksState.NoChecks,
            });

        public Task SquashMergeAsync(PrRef pr, MergeRequest merge, CancellationToken cancellationToken)
        {
            if (MergeFailure is not null)
            {
                throw MergeFailure;
            }

            Merges.Add(merge);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeReads : IGitHubPrReadModel
    {
        public PrChecksRollup Checks { get; set; } = PrChecksRollup.Passing;

        public Task<PrRef?> ResolveAsync(string prId, CancellationToken cancellationToken) =>
            Task.FromResult<PrRef?>(prId == _prId ? _pr : null);

        public Task<PrListReply> ListAsync(PrListRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new PrListReply(
                [new PrListRow(_prId, "codex-alchemist-dev/bazaar", 42, "Add x", "bot", PrAuthorKind.Agent, false, _head, Checks, 5)], null));

        public Task<PrSummaryReply?> GetSummaryAsync(string prId, PrRef pr, CancellationToken cancellationToken) =>
            Task.FromResult<PrSummaryReply?>(new PrSummaryReply(prId, _head, "text", [new PrChangedFile("src/a.cs", "modified", 3, 2)], Checks, true, null));

        public Task<PrChecksRollup> GetChecksAsync(PrRef pr, string headSha, CancellationToken cancellationToken) => Task.FromResult(Checks);
    }

    private sealed class SingleKeyRegistry(OwnerDeviceKey key) : IOwnerKeyRegistry
    {
        public Task<OwnerDeviceKey?> FindAsync(string deviceId, CancellationToken ct = default) =>
            Task.FromResult(deviceId == key.DeviceId ? key : null);
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var deviceKey = new OwnerDeviceKey(_ownerHuman.ToString("D"), _ownerDeviceId, Key.ExportSubjectPublicKeyInfo());
            var verifier = new OwnerSignatureVerifier(new SingleKeyRegistry(deviceKey), new InMemoryOwnerNonceStore());
            Adapter.ChecksSource = () => Reads.Checks;
            Merge = new PrMergeWireHandler(Adapter, Reads, new OwnerSignedMerge(Adapter, Adapter, verifier));
        }

        public ECDsa Key { get; }
        public FakeAdapter Adapter { get; } = new();
        public FakeReads Reads { get; } = new();
        public PrMergeWireHandler Merge { get; }

        public void Dispose() => Key.Dispose();
    }

    private static WireRequestContext Request<T>(IWireConnection connection, string type, T payload) =>
        new(connection, new Envelope(1, type, EnvelopeKind.Request, "req-1", null, null, null, null, _codec.EncodePayload(payload)));

    private static T Decode<T>(WireReply reply)
    {
        Assert.True(reply.IsOk, reply.Error?.Message);
        var envelope = new Envelope(1, "reply", EnvelopeKind.Response, "req-1", null, null, null, null, reply.Payload);
        return _codec.DecodePayload<T>(envelope);
    }

    private static PrMergeRequest SignedMerge(ECDsa signer, string approvedHead = _head, string requestHead = _head, byte nonceSeed = 7)
    {
        const string title = "Add x (#42)";
        const string message = "Body";
        var now = DateTimeOffset.UtcNow;
        var payload = new OwnerPayload(
            OwnerMergeArgs.Action,
            Guid.CreateVersion7(),
            OwnerMergeArgs.Hash(_pr, new MergeRequest(approvedHead, title, message)),
            approvedHead,
            _ownerHuman,
            _ownerDeviceId,
            now.AddSeconds(-30),
            now.AddMinutes(4),
            Enumerable.Repeat(nonceSeed, OwnerSignedPayload.NonceLength).ToArray());
        var signature = signer.SignData(OwnerSignedPayload.Build(payload), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var wire = new PrOwnerPayload(payload.Action, payload.ApprovalId, payload.ArgsHash, payload.HeadSha, payload.HumanId, payload.DeviceId, payload.IssuedAt, payload.ExpiresAt, payload.Nonce);
        return new PrMergeRequest(_prId, requestHead, title, message, wire, signature);
    }

    private static async Task<PrMergeReply> MergeAsync(Fixture f, PrMergeRequest request) =>
        Decode<PrMergeReply>(await f.Merge.HandleAsync(Request(new FakeConnection("owner"), WireTypes.PrMerge, request), Ct));

    // ---- read side ----

    [Fact]
    public async Task List_and_summary_answer_the_owner_from_the_read_model()
    {
        var reads = new FakeReads();
        var owner = new FakeConnection("owner");

        var list = Decode<PrListReply>(await new PrListWireHandler(reads).HandleAsync(
            Request(owner, WireTypes.PrList, new PrListRequest(null, PrListState.Open, null, null)), Ct));
        var summary = Decode<PrSummaryReply>(await new PrSummaryWireHandler(reads).HandleAsync(
            Request(owner, WireTypes.PrSummary, new PrSummaryRequest(_prId)), Ct));

        Assert.Equal(_prId, Assert.Single(list.Rows).PrId);
        Assert.Equal(_head, summary.HeadSha);
        Assert.True(summary.ReviewerApproved);
    }

    [Fact]
    public async Task An_unknown_pr_summary_and_a_non_owner_are_both_not_found()
    {
        var reads = new FakeReads();
        var handler = new PrSummaryWireHandler(reads);

        var unknown = await handler.HandleAsync(Request(new FakeConnection("owner"), WireTypes.PrSummary, new PrSummaryRequest("nope")), Ct);
        var agent = await handler.HandleAsync(Request(new FakeConnection("agent"), WireTypes.PrSummary, new PrSummaryRequest(_prId)), Ct);

        Assert.Equal(WireErrorCodes.NotFound, unknown.Error!.Code);
        Assert.Equal(WireErrorCodes.NotFound, agent.Error!.Code);
    }

    [Fact]
    public async Task Hunks_at_the_current_head_are_parsed_and_keyed_by_it()
    {
        var adapter = new FakeAdapter();
        var handler = new PrHunksWireHandler(adapter, new FakeReads());

        var reply = Decode<PrHunksReply>(await handler.HandleAsync(
            Request(new FakeConnection("owner"), WireTypes.PrHunks, new PrHunksRequest(_prId, _head, "src/a.cs", null)), Ct));

        Assert.Equal(PrHunksStatus.Ok, reply.Status);
        Assert.Equal(_head, reply.HeadSha);
        Assert.Equal(2, reply.Hunks.Count);
        Assert.Equal((1, 2, 1, 3), (reply.Hunks[0].OldStart, reply.Hunks[0].OldCount, reply.Hunks[0].NewStart, reply.Hunks[0].NewCount));
        Assert.Equal((10, 1, 11, 1), (reply.Hunks[1].OldStart, reply.Hunks[1].OldCount, reply.Hunks[1].NewStart, reply.Hunks[1].NewCount));
        Assert.Equal([" a", "-b", "+c", "+d"], reply.Hunks[0].Lines);
    }

    [Fact]
    public async Task Hunks_for_a_stale_head_reply_stale_with_the_current_sha_and_no_hunks()
    {
        var adapter = new FakeAdapter { Head = _moved };
        var handler = new PrHunksWireHandler(adapter, new FakeReads());

        var reply = Decode<PrHunksReply>(await handler.HandleAsync(
            Request(new FakeConnection("owner"), WireTypes.PrHunks, new PrHunksRequest(_prId, _head, "src/a.cs", null)), Ct));

        Assert.Equal(PrHunksStatus.Stale, reply.Status);
        Assert.Equal(_moved, reply.HeadSha);
        Assert.Empty(reply.Hunks);
        Assert.Equal(0, adapter.FileCalls);
    }

    [Fact]
    public async Task Hunks_stale_from_the_adapter_race_also_reply_stale()
    {
        var adapter = new RacingAdapter();
        var handler = new PrHunksWireHandler(adapter, new FakeReads());

        var reply = Decode<PrHunksReply>(await handler.HandleAsync(
            Request(new FakeConnection("owner"), WireTypes.PrHunks, new PrHunksRequest(_prId, _head, "src/a.cs", null)), Ct));

        Assert.Equal(PrHunksStatus.Stale, reply.Status);
        Assert.Equal(_moved, reply.HeadSha);
        Assert.Empty(reply.Hunks);
    }

    private sealed class RacingAdapter : IGitHubAdapter
    {
        public Task<PullRequestRef> OpenDraftPrAsync(OpenPrRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<PrFile>> GetPrFilesAsync(PrRef pr, string headSha, CancellationToken cancellationToken) =>
            throw new GitHubStaleHeadException(pr.Number, headSha, _moved);
        public Task PostReviewAsync(PrRef pr, ReviewRequest review, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string> GetHeadShaAsync(PrRef pr, CancellationToken cancellationToken) => Task.FromResult(_head);
        public Task<PrChecksState> GetChecksStateAsync(PrRef pr, string headSha, CancellationToken cancellationToken) => Task.FromResult(PrChecksState.Passing);
    }

    // ---- review ----

    [Theory]
    [InlineData("approve")]
    [InlineData("APPROVE")]
    [InlineData("")]
    public async Task Review_refuses_approve_and_posts_nothing(string reviewEvent)
    {
        var adapter = new FakeAdapter();
        var handler = new PrReviewWireHandler(adapter, new FakeReads());

        var reply = Decode<PrReviewReply>(await handler.HandleAsync(
            Request(new FakeConnection("owner"), WireTypes.PrReview, new PrReviewRequest(_prId, _head, reviewEvent, "lgtm")), Ct));

        Assert.Equal(PrReviewStatus.Refused, reply.Status);
        Assert.Empty(adapter.Reviews);
    }

    [Theory]
    [InlineData(PrReviewEvents.Comment, "COMMENT")]
    [InlineData(PrReviewEvents.RequestChanges, "REQUEST_CHANGES")]
    public async Task Review_applies_comment_and_request_changes_at_the_current_head(string reviewEvent, string githubEvent)
    {
        var adapter = new FakeAdapter();
        var handler = new PrReviewWireHandler(adapter, new FakeReads());

        var reply = Decode<PrReviewReply>(await handler.HandleAsync(
            Request(new FakeConnection("owner"), WireTypes.PrReview, new PrReviewRequest(_prId, _head, reviewEvent, "note")), Ct));

        Assert.Equal(PrReviewStatus.Applied, reply.Status);
        var posted = Assert.Single(adapter.Reviews);
        Assert.Equal(githubEvent, posted.Event);
        Assert.Equal(_head, posted.HeadSha);
    }

    [Fact]
    public async Task Review_at_a_stale_head_replies_stale_and_posts_nothing()
    {
        var adapter = new FakeAdapter { Head = _moved };
        var handler = new PrReviewWireHandler(adapter, new FakeReads());

        var reply = Decode<PrReviewReply>(await handler.HandleAsync(
            Request(new FakeConnection("owner"), WireTypes.PrReview, new PrReviewRequest(_prId, _head, PrReviewEvents.Comment, "note")), Ct));

        Assert.Equal(PrReviewStatus.Stale, reply.Status);
        Assert.Equal(_moved, reply.HeadSha);
        Assert.Empty(adapter.Reviews);
    }

    // ---- merge ----

    [Fact]
    public async Task Merge_happy_path_squash_merges_once_through_the_owner_gate()
    {
        using var f = new Fixture();

        var reply = await MergeAsync(f, SignedMerge(f.Key));

        Assert.Equal(PrMergeStatus.Merged, reply.Status);
        var merged = Assert.Single(f.Adapter.Merges);
        Assert.Equal(_head, merged.HeadSha);
        Assert.Equal("Add x (#42)", merged.CommitTitle);
    }

    [Fact]
    public async Task Merge_with_a_stale_head_replies_stale_with_the_current_sha_and_merges_nothing()
    {
        using var f = new Fixture();
        f.Adapter.Head = _moved;

        var reply = await MergeAsync(f, SignedMerge(f.Key));

        Assert.Equal(PrMergeStatus.Stale, reply.Status);
        Assert.Equal(_moved, reply.HeadSha);
        Assert.Empty(f.Adapter.Merges);
    }

    [Theory]
    [InlineData(PrChecksRollup.Failing)]
    [InlineData(PrChecksRollup.Pending)]
    [InlineData(PrChecksRollup.None)]
    public async Task Merge_with_checks_not_passing_replies_checks_failed_and_merges_nothing(PrChecksRollup checks)
    {
        using var f = new Fixture();
        f.Reads.Checks = checks;

        var reply = await MergeAsync(f, SignedMerge(f.Key));

        Assert.Equal(PrMergeStatus.ChecksFailed, reply.Status);
        Assert.Empty(f.Adapter.Merges);
    }

    [Theory]
    [InlineData(HttpStatusCode.MethodNotAllowed, PrMergeStatus.NotMergeable)]
    [InlineData(HttpStatusCode.UnprocessableEntity, PrMergeStatus.NotMergeable)]
    [InlineData(HttpStatusCode.Conflict, PrMergeStatus.Conflict)]
    public async Task Merge_refused_by_github_replies_with_its_own_code_not_a_generic_failure(HttpStatusCode status, PrMergeStatus expected)
    {
        using var f = new Fixture();
        f.Adapter.MergeFailure = new GitHubApiException(status, "Required status check is expected.", "merge failed");

        var reply = await MergeAsync(f, SignedMerge(f.Key));

        Assert.Equal(expected, reply.Status);
        Assert.Contains("Required status check", reply.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Merge_hitting_a_github_rate_limit_stays_a_wire_rate_limited_error()
    {
        using var f = new Fixture();
        f.Adapter.MergeFailure = new GitHubRateLimitedException(1, DateTimeOffset.UtcNow.AddMinutes(1), HttpStatusCode.Forbidden, "limited");

        var reply = await f.Merge.HandleAsync(Request(new FakeConnection("owner"), WireTypes.PrMerge, SignedMerge(f.Key)), Ct);

        Assert.False(reply.IsOk);
        Assert.Equal(WireErrorCodes.RateLimited, reply.Error?.Code);
    }

    [Fact]
    public async Task Merge_with_a_forged_signature_replies_bad_signature()
    {
        using var f = new Fixture();
        using var forger = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var reply = await MergeAsync(f, SignedMerge(forger));

        Assert.Equal(PrMergeStatus.BadSignature, reply.Status);
        Assert.Empty(f.Adapter.Merges);
    }

    [Fact]
    public async Task A_replayed_approval_replies_bad_signature_and_merges_only_once()
    {
        using var f = new Fixture();
        var request = SignedMerge(f.Key);

        var first = await MergeAsync(f, request);
        var replay = await MergeAsync(f, request);

        Assert.Equal(PrMergeStatus.Merged, first.Status);
        Assert.Equal(PrMergeStatus.BadSignature, replay.Status);
        Assert.Single(f.Adapter.Merges);
    }

    [Fact]
    public async Task A_red_merge_is_refused_and_the_owner_must_approve_again()
    {
        // The gate verifies the signature before it reads the head and the checks (doc 21, doc 32 section 5a), so a refused
        // merge has spent the single-use nonce and the same signed request cannot be replayed.
        using var f = new Fixture();
        var request = SignedMerge(f.Key);
        f.Reads.Checks = PrChecksRollup.Failing;
        var red = await MergeAsync(f, request);
        f.Reads.Checks = PrChecksRollup.Passing;

        var replay = await MergeAsync(f, request);

        Assert.Equal(PrMergeStatus.ChecksFailed, red.Status);
        Assert.Equal(PrMergeStatus.BadSignature, replay.Status);
        Assert.Empty(f.Adapter.Merges);
    }

    [Fact]
    public async Task Approval_for_a_different_merge_is_refused()
    {
        using var f = new Fixture();
        var request = SignedMerge(f.Key) with { CommitTitle = "A different title" };

        var reply = await MergeAsync(f, request);

        Assert.Equal(PrMergeStatus.Refused, reply.Status);
        Assert.Empty(f.Adapter.Merges);
    }

    [Fact]
    public async Task An_agent_principal_never_reaches_merge()
    {
        using var f = new Fixture();

        var reply = await f.Merge.HandleAsync(Request(new FakeConnection("agent"), WireTypes.PrMerge, SignedMerge(f.Key)), Ct);

        Assert.Equal(WireErrorCodes.NotFound, reply.Error!.Code);
        Assert.Empty(f.Adapter.Merges);
    }

    [Fact]
    public void Handlers_serve_the_documented_message_types()
    {
        var reads = new FakeReads();
        var adapter = new FakeAdapter();
        Assert.Equal("pr.list", new PrListWireHandler(reads).Type);
        Assert.Equal("pr.summary", new PrSummaryWireHandler(reads).Type);
        Assert.Equal("pr.hunks", new PrHunksWireHandler(adapter, reads).Type);
        Assert.Equal("pr.review", new PrReviewWireHandler(adapter, reads).Type);
    }
}
