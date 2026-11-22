using System.Security.Cryptography;
using Bazaar.Approvals;
using Bazaar.Community.Actions;
using Bazaar.Community.Drafts;
using Bazaar.Community.Profile;
using Bazaar.Community.Tests.Actions;
using Bazaar.Contracts.Approvals;
using Bazaar.Contracts.Community;
using Bazaar.Server.Community;
using Bazaar.Server.Security.Approvals;

namespace Bazaar.Community.Tests.Drafts;

/// <summary>Fixed-time clock the tests move by hand.</summary>
internal sealed class TestClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed class FakeDraftStore : ICommunityDraftStore
{
    private readonly Dictionary<Guid, CommunityDraft> _drafts = [];

    public IReadOnlyCollection<CommunityDraft> All => _drafts.Values;

    public void Add(CommunityDraft draft) => _drafts[draft.DraftId] = draft;

    public Task<CommunityDraft?> GetAsync(Guid draftId, CancellationToken ct = default) =>
        Task.FromResult(_drafts.GetValueOrDefault(draftId));

    public Task<bool> TryReplaceAsync(Guid draftId, CommunityDraftStatus expected, CommunityDraft updated, CancellationToken ct = default)
    {
        if (!_drafts.TryGetValue(draftId, out var current) || current.Status != expected) return Task.FromResult(false);
        _drafts[draftId] = updated;
        return Task.FromResult(true);
    }

    public Task<bool> TrySupersedeAsync(Guid originalId, CommunityDraft replacement, CancellationToken ct = default)
    {
        if (!_drafts.TryGetValue(originalId, out var current) || current.Status != CommunityDraftStatus.Pending) return Task.FromResult(false);
        _drafts[originalId] = current with { Status = CommunityDraftStatus.Edited, SupersededBy = replacement.DraftId };
        _drafts[replacement.DraftId] = replacement;
        return Task.FromResult(true);
    }
}

internal sealed class FakeAdapter(string platform = "github") : ICommunityAdapter
{
    public string Platform { get; } = platform;

    public List<(OutboundPost Post, ApprovalId Approval)> Posts { get; } = [];

    /// <summary>Failures to throw before succeeding, one per call.</summary>
    public Queue<CommunityPostException> Failures { get; } = new();

    public Task<PostReceipt> PostAsync(OutboundPost post, ApprovalId approval, CancellationToken cancellationToken)
    {
        if (Failures.TryDequeue(out var failure)) throw failure;
        Posts.Add((post, approval));
        return Task.FromResult(new PostReceipt("ext-42", new Uri("https://example.test/issues/42"), Reused: false));
    }
}

internal sealed class FakePolicySource(PlatformPolicy? policy) : ICommunityPolicySource
{
    public Task<PlatformPolicy?> FindAsync(string platform, CancellationToken ct = default) => Task.FromResult(policy);
}

/// <summary>A wired handler with a real owner key, real signatures and in-memory fakes around it.</summary>
internal sealed class DraftFixture : IDisposable
{
    public const string DeviceId = "phone-1";

    public static readonly DateTimeOffset Start = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public DraftFixture(bool gatesPass = true)
    {
        var keys = new InMemoryOwnerKeyRegistry();
        keys.Register(new OwnerDeviceKey("owner-1", DeviceId, _key.ExportSubjectPublicKeyInfo()));
        Keys = keys;
        Policy = new PlatformPolicy(
            "github",
            PolicyStatus.Verified,
            new HashSet<CommunityAction> { CommunityAction.NewIssue },
            "disclose",
            new Uri("https://example.org/rules"),
            DateOnly.FromDateTime(Start.UtcDateTime).AddDays(-1));
        Sql = new InMemoryCommunityActionSql();
        Audit = new FakeAuditLog();
        Handler = new CommunityDraftDecisionHandler(
            Store, Keys, [Adapter], new CommunityActionStore(Sql, Audit), new FakePolicySource(Policy), Disclosure, Clock);
        Draft = NewDraft(gatesPass);
        Store.Add(Draft);
    }

    public TestClock Clock { get; } = new(Start);

    public FakeDraftStore Store { get; } = new();

    public FakeAdapter Adapter { get; } = new();

    public InMemoryOwnerKeyRegistry Keys { get; }

    public PlatformPolicy Policy { get; }

    public InMemoryCommunityActionSql Sql { get; }

    public FakeAuditLog Audit { get; }

    public DisclosureOptions Disclosure { get; } = new();

    public CommunityDraftDecisionHandler Handler { get; }

    public CommunityDraft Draft { get; }

    public static BotProfile Profile { get; } = new("agent-1", "github", "Ada", "Sam", "sam@example.test", new Uri("https://example.test/disclosure"));

    public CommunityDraft NewDraft(bool gatesPass)
    {
        var body = BotProfileBuilder.AppendFooter("Found a crash on start; steps below.", Profile, Disclosure);
        var post = new OutboundPost(Guid.CreateVersion7(), "github", "issue", "owner/repo", "Crash on start", body, DraftApprovalHash.BodySha256Hex(body));
        var gate = new Bazaar.Community.Gates.DraftPost(
            "github", "issue", "Found a crash on start; steps below.",
            gatesPass ? ["crash on start"] : [],
            [], null, null, false,
            gatesPass ? ["https://example.org/issues/1"] : []);
        var failures = gatesPass ? [] : Bazaar.Community.Gates.QualityGates.Evaluate(gate, Policy, DateOnly.FromDateTime(Start.UtcDateTime)).Failures;
        return new CommunityDraft(
            post.DraftId, "agent-1", "sponsor-1", CommunityAction.NewIssue, post, gate, Profile,
            Guid.CreateVersion7(), DraftApprovalHash.Compute(post), Start.AddHours(24), failures);
    }

    /// <summary>A decision signed by the owner's device the way the client signs it: approve only for ApproveOnce.</summary>
    public DraftReviewDecisionDto Sign(CommunityDraft draft, ApprovalDecisionKind kind, string? edited = null, string deviceId = DeviceId, ECDsa? key = null)
    {
        var signed = kind == ApprovalDecisionKind.ApproveOnce ? ApprovalDecision.Approve : ApprovalDecision.Deny;
        var message = ApprovalSignature.Message(draft.ApprovalId, signed, draft.ApprovalHash, draft.ApprovalExpiresAt, deviceId);
        var signature = (key ?? _key).SignData(message, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return new DraftReviewDecisionDto(
            draft.DraftId,
            new ApprovalDecisionDto(draft.ApprovalId, kind, deviceId, Convert.ToBase64String(signature), Start),
            edited);
    }

    public void Dispose() => _key.Dispose();
}
