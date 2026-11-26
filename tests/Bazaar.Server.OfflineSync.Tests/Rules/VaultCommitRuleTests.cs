using System.Text.Json;
using Bazaar.OfflineSync;
using Bazaar.Server.OfflineSync.Rules;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.OfflineSync.Tests.Rules;

public sealed class VaultCommitRuleTests
{
    private const string _head = "1111111111111111111111111111111111111111";
    private const string _base = "2222222222222222222222222222222222222222";
    private const string _branch = "agent/haiku-7";
    private const string _repository = "vault-main";

    [Fact]
    public async Task Merged_maps_to_applied_with_the_new_head()
    {
        var merger = new FakeVaultMerger(new VaultMergeResult.Merged(_head));
        var write = Write(_branch, _head, _base);

        var outcome = await new VaultCommitRule(merger).MergeAsync(write, CancellationToken.None);

        Assert.Equal(OfflineOutcomeKind.Applied, outcome.Kind);
        Assert.Equal(_head, outcome.ServerVersion);
        Assert.Equal(write.WriteId, outcome.WriteId);
    }

    [Fact]
    public async Task Merger_receives_the_intent_built_from_the_payload()
    {
        var merger = new FakeVaultMerger(new VaultMergeResult.Merged(_head));
        var write = Write(_branch, _head, _base);

        await new VaultCommitRule(merger).MergeAsync(write, CancellationToken.None);

        var intent = Assert.Single(merger.Intents);
        Assert.Equal(new VaultPushIntent(write.WriteId, RuleTestKit.DeviceA, _repository, _branch, _head, _base), intent);
    }

    [Fact]
    public async Task ConflictBranch_maps_to_conflict_and_names_the_branch_and_review_item()
    {
        var merger = new FakeVaultMerger(new VaultMergeResult.ConflictBranch("conflict/device-a/20261118", "review-42"));

        var outcome = await new VaultCommitRule(merger).MergeAsync(Write(_branch, _head, _base), CancellationToken.None);

        Assert.Equal(OfflineOutcomeKind.Conflict, outcome.Kind);
        Assert.Contains("conflict/device-a/20261118", outcome.Reason, StringComparison.Ordinal);
        Assert.Contains("review-42", outcome.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rejected_maps_to_rejected_with_the_merger_reason()
    {
        var merger = new FakeVaultMerger(new VaultMergeResult.Rejected("Constitution changes need the owner."));

        var outcome = await new VaultCommitRule(merger).MergeAsync(Write(_branch, _head, _base), CancellationToken.None);

        Assert.Equal(OfflineOutcomeKind.Rejected, outcome.Kind);
        Assert.Equal("Constitution changes need the owner.", outcome.Reason);
    }

    [Fact]
    public async Task MalformedPayload_is_rejected_before_the_merger_runs()
    {
        var merger = new FakeVaultMerger(new VaultMergeResult.Merged(_head));
        var write = RuleTestKit.Write(OfflineDataClass.VaultCommit, _repository, new byte[] { 0x7b, 0x7b });

        var outcome = await new VaultCommitRule(merger).MergeAsync(write, CancellationToken.None);

        Assert.Equal(OfflineOutcomeKind.Rejected, outcome.Kind);
        Assert.Empty(merger.Intents);
    }

    [Fact]
    public async Task PayloadNamingAnotherDevice_is_rejected()
    {
        var merger = new FakeVaultMerger(new VaultMergeResult.Merged(_head));
        var payload = new VaultCommitPayload(RuleTestKit.DeviceB, _branch, _head, _base);
        var write = RuleTestKit.Write(OfflineDataClass.VaultCommit, _repository, Json(payload), hlc: RuleTestKit.At(1000));

        var outcome = await new VaultCommitRule(merger).MergeAsync(write, CancellationToken.None);

        Assert.Equal(OfflineOutcomeKind.Rejected, outcome.Kind);
        Assert.Empty(merger.Intents);
    }

    [Theory]
    [InlineData("")]
    [InlineData("-force")]
    [InlineData("agent/../main")]
    [InlineData("agent/with space")]
    [InlineData("agent/with\nnewline")]
    public async Task InvalidBranch_is_rejected(string branch)
    {
        var merger = new FakeVaultMerger(new VaultMergeResult.Merged(_head));

        var outcome = await new VaultCommitRule(merger).MergeAsync(Write(branch, _head, _base), CancellationToken.None);

        Assert.Equal(OfflineOutcomeKind.Rejected, outcome.Kind);
        Assert.Empty(merger.Intents);
    }

    [Theory]
    [InlineData("ABCDEF0000000000000000000000000000000000")]
    [InlineData("1234")]
    [InlineData("zz11111111111111111111111111111111111111")]
    public async Task InvalidCommitId_is_rejected(string commit)
    {
        var merger = new FakeVaultMerger(new VaultMergeResult.Merged(_head));

        var outcome = await new VaultCommitRule(merger).MergeAsync(Write(_branch, commit, _base), CancellationToken.None);

        Assert.Equal(OfflineOutcomeKind.Rejected, outcome.Kind);
        Assert.Empty(merger.Intents);
    }

    [Fact]
    public async Task Sha256CommitIds_are_accepted()
    {
        var sha256 = new string('a', 64);
        var merger = new FakeVaultMerger(new VaultMergeResult.Merged(sha256));

        var outcome = await new VaultCommitRule(merger).MergeAsync(Write(_branch, sha256, sha256), CancellationToken.None);

        Assert.Equal(OfflineOutcomeKind.Applied, outcome.Kind);
    }

    [Fact]
    public void Payload_round_trips_through_the_source_generated_context()
    {
        var payload = new VaultCommitPayload(RuleTestKit.DeviceA, _branch, _head, _base);

        var bytes = Json(payload);
        var text = System.Text.Encoding.UTF8.GetString(bytes.Span);

        Assert.Contains("\"headCommit\"", text, StringComparison.Ordinal);
        Assert.Equal(payload, VaultCommitPayloadCodec.Read(bytes));
    }

    [Fact]
    public void Registration_is_skipped_without_a_vault_merger()
    {
        var services = new ServiceCollection().AddBazaarVaultCommitRule();

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IOfflineMergeRule));
    }

    [Fact]
    public void Registration_adds_the_rule_when_a_vault_merger_is_registered()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IVaultOfflineMerger>(new FakeVaultMerger(new VaultMergeResult.Merged(_head)));
        services.AddBazaarVaultCommitRule();

        using var provider = services.BuildServiceProvider();
        var rule = Assert.Single(provider.GetServices<IOfflineMergeRule>());
        Assert.IsType<VaultCommitRule>(rule);
    }

    [Fact]
    public async Task Without_a_rule_the_processor_reports_module_unavailable()
    {
        var processor = Make.Processor(new InMemoryOfflineSyncStore(), []);
        var batch = Make.Batch(RuleTestKit.DeviceA, [Make.Write(RuleTestKit.DeviceA, 1, OfflineDataClass.VaultCommit)]);

        var result = await processor.ProcessAsync(batch, TestContext.Current.CancellationToken);

        Assert.Equal(OfflineOutcomeKind.ModuleUnavailable, Assert.Single(result.Outcomes).Kind);
    }

    private static OfflineWrite Write(string branch, string head, string baseCommit) =>
        RuleTestKit.Write(OfflineDataClass.VaultCommit, _repository, Json(new VaultCommitPayload(RuleTestKit.DeviceA, branch, head, baseCommit)));

    private static ReadOnlyMemory<byte> Json(VaultCommitPayload payload) =>
        JsonSerializer.SerializeToUtf8Bytes(payload, VaultCommitPayloadJsonContext.Default.VaultCommitPayload);

    private sealed class FakeVaultMerger(VaultMergeResult result) : IVaultOfflineMerger
    {
        public List<VaultPushIntent> Intents { get; } = [];

        public ValueTask<VaultMergeResult> MergeAsync(VaultPushIntent intent, CancellationToken cancellationToken)
        {
            Intents.Add(intent);
            return ValueTask.FromResult(result);
        }
    }
}
