using Bazaar.Server.Modules.Health;

namespace Bazaar.Server.Portability.Tests;

public class PortabilityHealthTests
{
    private sealed class Jobs(ImportJobHealthSnapshot? snapshot, bool throws = false) : IImportJobHealthSource
    {
        public ValueTask<ImportJobHealthSnapshot> ReadAsync(CancellationToken ct) =>
            throws ? throw new InvalidOperationException("secret detail") : ValueTask.FromResult(snapshot!);
    }

    private sealed class Dep(string id, ModuleHealthStatus status, bool throws = false) : IPortabilityDependencyProbe
    {
        public string DependencyId => id;

        public ValueTask<ModuleHealthCheck> ProbeAsync(CancellationToken ct) =>
            throws ? throw new InvalidOperationException("boom") : ValueTask.FromResult(new ModuleHealthCheck(status, "x", DateTimeOffset.UnixEpoch));
    }

    private sealed class FixedTime(DateTimeOffset at) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => at;
    }

    private sealed class FailingImporter(bool throws = false) : IMappingImporter
    {
        public string Id => "failing.test";
        public string Version => "1";
        public string FormatId => "failing";
        public ImporterDeclaration Declaration => new("failing", [ImportTargetKind.VaultFile]);
        public ImporterSelfTestResult SelfTest() => throws ? throw new InvalidOperationException("boom") : new(false, "bad");
        public MappingResult Map(SourceRecord record, MappingRules rules) => MappingResult.Skip("n/a");
        public DetectResult Detect(ImportSource source) => throw new NotSupportedException();
        public IReadOnlyList<SourceRecord> Parse(ImportSource source) => throw new NotSupportedException();
    }

    private static readonly ImportJobHealthSnapshot Clean = new(0, 0, []);

    [Fact]
    public async Task Liveness_and_module_id()
    {
        var health = new PortabilityHealth([]);
        Assert.Equal("portability", health.ModuleId);
        Assert.Equal(ModuleHealthStatus.Healthy, (await health.LivenessAsync(TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task Readiness_is_healthy_without_a_job_source_or_with_clean_jobs()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(ModuleHealthStatus.Healthy, (await new PortabilityHealth([]).ReadinessAsync(ct)).Status);
        Assert.Equal(ModuleHealthStatus.Healthy, (await new PortabilityHealth([], new Jobs(Clean)).ReadinessAsync(ct)).Status);
    }

    [Fact]
    public async Task Readiness_is_degraded_for_each_alert_kind()
    {
        var health = new PortabilityHealth([], new Jobs(new(1, 2, ["trello.json"])));
        var check = await health.ReadinessAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ModuleHealthStatus.Degraded, check.Status);
        Assert.Contains("failed verification", check.Reason);
        Assert.Contains("stalled", check.Reason);
        Assert.Contains("crash loop", check.Reason);
        Assert.DoesNotContain("trello", check.Reason);
    }

    [Fact]
    public void Alerts_are_pure_and_ordered()
    {
        Assert.Empty(PortabilityJobAlerts.Evaluate(Clean));
        var kinds = PortabilityJobAlerts.Evaluate(new(1, 1, ["a"])).Select(a => a.Kind);
        Assert.Equal(["failed_verify", "stalled", "parser_crash_loop"], kinds);
    }

    [Fact]
    public async Task Readiness_is_unknown_when_the_source_throws_and_hides_the_message()
    {
        var check = await new PortabilityHealth([], new Jobs(null, throws: true)).ReadinessAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ModuleHealthStatus.Unknown, check.Status);
        Assert.DoesNotContain("secret", check.Reason);
    }

    [Fact]
    public async Task Dependencies_are_reported_and_a_throwing_probe_is_unknown()
    {
        var ct = TestContext.Current.CancellationToken;
        var health = new PortabilityHealth([], dependencies: [new Dep("sandbox", ModuleHealthStatus.Failing), new Dep("other", ModuleHealthStatus.Healthy, throws: true)]);
        var deps = await health.DependenciesAsync(ct);
        Assert.Equal(["sandbox", "other"], deps.Select(d => d.DependencyId));
        Assert.Equal(ModuleHealthStatus.Failing, deps[0].Check.Status);
        Assert.Equal(ModuleHealthStatus.Unknown, deps[1].Check.Status);
        Assert.Empty(await new PortabilityHealth([]).DependenciesAsync(ct));
    }

    [Fact]
    public async Task Self_test_with_no_importers_is_unknown_not_healthy()
    {
        var check = await new PortabilityHealth([]).SelfTestAsync(ModuleSelfTestScope.Quick, TestContext.Current.CancellationToken);
        Assert.Equal(ModuleHealthStatus.Unknown, check.Status);
    }

    [Theory]
    [InlineData(ModuleSelfTestScope.Quick)]
    [InlineData(ModuleSelfTestScope.Full)]
    public async Task Self_test_passes_for_the_built_in_importers(ModuleSelfTestScope scope)
    {
        var health = new PortabilityHealth([new TrelloMappingImporter(), new DocxMappingImporter(), new ObsidianMappingImporter(), new MilanoteMappingImporter(), new MiroMappingImporter()]);
        var check = await health.SelfTestAsync(scope, TestContext.Current.CancellationToken);
        Assert.Equal(ModuleHealthStatus.Healthy, check.Status);
    }

    [Fact]
    public async Task Self_test_fails_and_names_only_the_importer_id()
    {
        var check = await new PortabilityHealth([new FailingImporter()]).SelfTestAsync(ModuleSelfTestScope.Quick, TestContext.Current.CancellationToken);
        Assert.Equal(ModuleHealthStatus.Failing, check.Status);
        Assert.Contains("failing.test", check.Reason);
        Assert.DoesNotContain("bad", check.Reason);
    }

    [Fact]
    public async Task Self_test_treats_a_throwing_importer_as_failed()
    {
        var check = await new PortabilityHealth([new FailingImporter(throws: true)]).SelfTestAsync(ModuleSelfTestScope.Quick, TestContext.Current.CancellationToken);
        Assert.Equal(ModuleHealthStatus.Failing, check.Status);
    }

    [Fact]
    public async Task Check_time_comes_from_the_injected_clock()
    {
        var at = new DateTimeOffset(2026, 11, 22, 0, 0, 0, TimeSpan.Zero);
        var check = await new PortabilityHealth([], time: new FixedTime(at)).LivenessAsync(TestContext.Current.CancellationToken);
        Assert.Equal(at, check.CheckedAt);
    }
}
