using System.Security.Cryptography;
using Bazaar.Approvals;
using Bazaar.Contracts.Approvals;
using Bazaar.Contracts.Community;
using Bazaar.Server.Community;
using Bazaar.Server.Community.Actions;
using Bazaar.Server.Community.Drafts;
using Bazaar.Server.Community.Gates;
using Bazaar.Server.Community.GitHub;
using Bazaar.Server.Community.GitHub.Bridge;
using Bazaar.Server.Community.Policy;
using Bazaar.Server.Community.Profile;
using Bazaar.Server.Host.Composition;
using Bazaar.Server.Security.Approvals;
using Bazaar.Server.Security.Audit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Bazaar.Server.Host.Tests;

/// <summary>
/// The community draft seams are bound in the host graph (doc 36 WP7 and WP8): the decision handler and the creator resolve, and a
/// decision for a repository with no configured GitHub installation is refused as a post refusal, not answered as "unavailable".
/// </summary>
public sealed class CommunityCompositionTests
{
    private const string _dbEnv = "BAZAAR_COMMUNITY_COMPOSITION_TEST_DB";
    private const string _tokenEnv = "BAZAAR_COMMUNITY_COMPOSITION_TEST_TOKEN";
    private const string _deviceId = "phone-1";
    private static readonly DateTimeOffset _start = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    private static HostApplicationBuilder NewBuilder(IDictionary<string, string?>? extra = null)
    {
        Environment.SetEnvironmentVariable(_dbEnv, "Host=localhost;Database=unused");
        Environment.SetEnvironmentVariable(_tokenEnv, "dev-secret-for-graph-test");
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        var settings = new Dictionary<string, string?>
        {
            ["Bazaar:Host:DataRoot"] = Path.Combine(Path.GetTempPath(), "bazaar-community-test-" + Guid.NewGuid().ToString("N")),
            ["Bazaar:Host:DatabaseConnectionReference"] = "env:" + _dbEnv,
            ["Bazaar:DevAuth:Enabled"] = "true",
            ["Bazaar:DevAuth:TokenSecret"] = "env:" + _tokenEnv,
        };
        if (extra is not null)
        {
            foreach (var pair in extra) settings[pair.Key] = pair.Value;
        }

        builder.Configuration.AddInMemoryCollection(settings);
        return builder;
    }

    private static void ClearEnvironment()
    {
        Environment.SetEnvironmentVariable(_dbEnv, null);
        Environment.SetEnvironmentVariable(_tokenEnv, null);
    }

    [Fact]
    public void The_handler_and_creator_resolve_from_the_host_container()
    {
        try
        {
            var builder = NewBuilder();
            builder.Services.AddBazaarHostApplication(builder.Configuration);
            using var host = builder.Build();
            var sp = host.Services;

            Assert.NotNull(sp.GetRequiredService<CommunityDraftDecisionHandler>());
            Assert.NotNull(sp.GetRequiredService<ICommunityDraftCreator>());
            Assert.IsType<PlatformPolicySource>(sp.GetRequiredService<ICommunityPolicySource>());
            Assert.NotNull(sp.GetRequiredService<CommunityActionStore>());
            Assert.NotNull(sp.GetRequiredService<IOwnerKeyRegistry>());
            Assert.Contains(sp.GetServices<ICommunityAdapter>(), a => a is GitHubCommunityAdapter);
            Assert.IsType<ConfiguredCommunityInstallationResolver>(sp.GetRequiredService<ICommunityInstallationResolver>());
            Assert.IsType<BridgeCommunityGitHubTokenSource>(sp.GetRequiredService<ICommunityGitHubTokenSource>());
        }
        finally
        {
            ClearEnvironment();
        }
    }

    [Fact]
    public async Task A_decision_with_no_installation_configured_is_refused_not_unavailable()
    {
        try
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var keys = new InMemoryOwnerKeyRegistry();
            keys.Register(new OwnerDeviceKey("owner-1", _deviceId, key.ExportSubjectPublicKeyInfo()));
            var store = new DraftStore();
            var sql = new ActionSql();

            // The persistent stores are swapped for in-memory ones (registered first, so the host's TryAdd keeps them). The adapter,
            // its HTTP API, the bridge, the installation resolver and the token provider are the host's own, with no installation configured.
            var builder = NewBuilder();
            builder.Services.AddSingleton<IOwnerKeyRegistry>(keys);
            builder.Services.AddSingleton<ICommunityDraftStore>(store);
            builder.Services.AddSingleton<ICommunityActionSql>(sql);
            builder.Services.AddSingleton<IAuditLog>(new NullAudit());
            builder.Services.AddSingleton<ICommunityPolicySource>(new FixedPolicy());
            builder.Services.AddSingleton<TimeProvider>(new FixedClock(_start));
            builder.Services.AddBazaarHostApplication(builder.Configuration);
            using var host = builder.Build();

            var draft = NewDraft();
            store.Add(draft);
            var handler = host.Services.GetRequiredService<CommunityDraftDecisionHandler>();

            var outcome = await handler.HandleAsync(draft.DraftId, Sign(draft, key), TestContext.Current.CancellationToken);

            Assert.Equal(DraftDecisionStatus.PostRefused, outcome.Status);
            Assert.Equal("post_refused", outcome.Code);
            Assert.Equal(CommunityDraftStatus.Failed, (await store.GetAsync(draft.DraftId, TestContext.Current.CancellationToken))!.Status);
            Assert.Equal("failed", Assert.Single(sql.Rows).Status);
        }
        finally
        {
            ClearEnvironment();
        }
    }

    private static CommunityDraft NewDraft()
    {
        var profile = new BotProfile("agent-1", "github", "Ada", "Sam", "sam@example.test", new Uri("https://example.test/disclosure"));
        var body = BotProfileBuilder.AppendFooter("Found a crash on start; steps below.", profile, new DisclosureOptions());
        var post = new OutboundPost(Guid.CreateVersion7(), "github", "issue", "owner/repo", "Crash on start", body, DraftApprovalHash.BodySha256Hex(body));
        var gate = new DraftPost("github", "issue", "Found a crash on start; steps below.", ["crash on start"], [], null, null, false, ["https://example.org/issues/1"]);
        return new CommunityDraft(
            post.DraftId, "agent-1", "sponsor-1", CommunityAction.NewIssue, post, gate, profile,
            Guid.CreateVersion7(), DraftApprovalHash.Compute(post), _start.AddHours(24), []);
    }

    private static DraftReviewDecisionDto Sign(CommunityDraft draft, ECDsa key)
    {
        var message = ApprovalSignature.Message(draft.ApprovalId, ApprovalDecision.Approve, draft.ApprovalHash, draft.ApprovalExpiresAt, _deviceId);
        var signature = key.SignData(message, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return new DraftReviewDecisionDto(
            draft.DraftId,
            new ApprovalDecisionDto(draft.ApprovalId, ApprovalDecisionKind.ApproveOnce, _deviceId, Convert.ToBase64String(signature), _start),
            null);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class NullAudit : IAuditLog
    {
        public Task AppendAsync(AuditEntry e, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FixedPolicy : ICommunityPolicySource
    {
        public Task<PlatformPolicy?> FindAsync(string platform, CancellationToken ct = default) =>
            Task.FromResult<PlatformPolicy?>(new PlatformPolicy(
                "github", PolicyStatus.Verified, new HashSet<CommunityAction> { CommunityAction.NewIssue }, "disclose",
                new Uri("https://example.org/rules"), DateOnly.FromDateTime(_start.UtcDateTime)));
    }

    private sealed class ActionSql : ICommunityActionSql
    {
        private readonly List<CommunityActionRecord> _rows = [];

        public IReadOnlyList<CommunityActionRecord> Rows => _rows;

        public Task<long> InsertAsync(CommunityActionRecord record, CancellationToken ct)
        {
            _rows.Add(record);
            return Task.FromResult((long)_rows.Count);
        }

        public Task<IReadOnlyList<CommunityActionRecord>> ListForAgentAsync(string agentId, string platform, int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<CommunityActionRecord>>(_rows);
    }

    private sealed class DraftStore : ICommunityDraftStore
    {
        private readonly Dictionary<Guid, CommunityDraft> _drafts = [];

        public void Add(CommunityDraft draft) => _drafts[draft.DraftId] = draft;

        public Task<CommunityDraft?> GetAsync(Guid draftId, CancellationToken ct = default) => Task.FromResult(_drafts.GetValueOrDefault(draftId));

        public Task<bool> TryReplaceAsync(Guid draftId, CommunityDraftStatus expected, CommunityDraft updated, CancellationToken ct = default)
        {
            if (!_drafts.TryGetValue(draftId, out var current) || current.Status != expected) return Task.FromResult(false);
            _drafts[draftId] = updated;
            return Task.FromResult(true);
        }

        public Task<bool> TrySupersedeAsync(Guid originalId, CommunityDraft replacement, CancellationToken ct = default) => Task.FromResult(false);

        public Task<bool> TryAddAsync(CommunityDraft draft, CancellationToken ct = default) => Task.FromResult(_drafts.TryAdd(draft.DraftId, draft));

        public Task<IReadOnlyList<CommunityDraft>> ListPendingAsync(DateTimeOffset openAt, int limit, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CommunityDraft>>([]);

        public Task<IReadOnlyList<Guid>> ExpireDueAsync(DateTimeOffset now, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Guid>>([]);
    }
}
