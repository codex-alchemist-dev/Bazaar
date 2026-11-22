using Bazaar.Server.Host.Components.Settings;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.Setup;
using Bazaar.Server.Setup.Host;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bazaar.Server.Host.Tests;

/// <summary>
/// The config-path door (doc 45 section 1.1): <c>--setup-plan</c> / <c>BAZAAR_SETUP_PLAN</c> applies a local plan file in-process, with no
/// bootstrap code, through the same loader and applier as the HTTP route. No database: the stores are in-memory fakes.
/// </summary>
public sealed class SetupPlanOnStartTests : IDisposable
{
    private const string _plan = """
        {
          "schemaVersion": 1,
          "install": {
            "companyName": "Example Studio",
            "companySlug": "example-studio",
            "locale": "en",
            "timezone": "Europe/London",
            "releaseChannel": "beta",
            "updatePolicy": { "security": "automatic", "other": "manual" }
          },
          "database": { "postgresMajor": 18, "dataRoot": "C:\\ProgramData\\Bazaar", "gcPartitionCount": 16 },
          "custody": {
            "kekProvider": "dpapi",
            "kekRef": "dpapi:C:\\ProgramData\\Bazaar\\kek.bin",
            "auditAnchorTarget": "https://anchor.example.invalid/bazaar-audit",
            "mergeCredential": { "kind": "owner_app", "appIdRef": "secret://bazaar/owner/app-id" }
          },
          "owner": {
            "displayName": "Owner",
            "requireSecondFactor": true,
            "recovery": { "printedCodes": false, "backupOwners": [] }
          },
          "privacy": { "profile": "private" },
          "backups": { "target": "file:/mnt/backup/bazaar", "drillIntervalDays": 30, "acknowledgeNoTarget": false },
          "models": {
            "sources": [
              { "id": "freellmapi", "keyRef": "secret://bazaar/providers/freellmapi/key" },
              { "id": "anthropic-owner", "keyRef": "secret://bazaar/providers/anthropic/key" }
            ],
            "defaultPreset": "free-lane"
          },
          "email": { "enabled": false },
          "scamMailProtocol": { "enabled": false },
          "company": { "source": "template:starter@1.0.0" },
          "modules": ["Bazaar.GC", "Bazaar.Constellation", "Bazaar.KODO"],
          "network": { "statusBind": "loopback", "statusPort": 8443 },
          "settings": { "host.shutdown_timeout_seconds": 20 }
        }
        """;

    private readonly string _dir = Directory.CreateTempSubdirectory("bazaar-plan-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Write(string json)
    {
        var path = Path.Combine(_dir, "bazaar-setup.json");
        File.WriteAllText(path, json);
        return path;
    }

    private static string PlanHash() => SetupPlanLoader.Load(_plan, RegistrySetupSettingCatalog.Known).PlanHash!;

    private sealed class Rig
    {
        public Rig(ISetupStep? step = null)
        {
            var services = new ServiceCollection();
            services.AddSingleton(TimeProvider.System);
            services.AddSingleton<IAuditLog>(new ListAuditLog(Audit));
            services.AddSingleton<ISetupInstanceLock>(new NoLock());
            services.AddSingleton<IApplyResultStore>(Results);
            new SettingsComponent().AddServices(services, new ConfigurationBuilder().Build());
            services.AddSingleton<ISetupSettingCatalog>(RegistrySetupSettingCatalog.Known);
            services.AddSingleton<ISetupSettingAudit>(sp => new SettingAuditSinkSetupAudit(sp.GetRequiredService<Bazaar.Server.Settings.ISettingAuditSink>()));
            services.AddSingleton<ISetupStep>(step ?? new OkStep());
            services.AddSetupApply();
            Provider = services.BuildServiceProvider();
        }

        public List<AuditEntry> Audit { get; } = [];

        public MemoryResults Results { get; } = new();

        public StopLifetime Lifetime { get; } = new();

        public SetupPlanStartOutcome Outcome { get; } = new();

        public ServiceProvider Provider { get; }

        public SetupPlanOnStartService Service(string? path, bool setupOnly) => new(
            new SetupPlanStartOptions(path, setupOnly),
            Provider.GetRequiredService<SetupApplier>(),
            Results,
            Provider.GetRequiredService<ISetupSettingCatalog>(),
            Outcome,
            Lifetime,
            NullLogger<SetupPlanOnStartService>.Instance);
    }

    private sealed class ListAuditLog(List<AuditEntry> rows) : IAuditLog
    {
        public Task AppendAsync(AuditEntry e, CancellationToken ct = default)
        {
            lock (rows) rows.Add(e);
            return Task.CompletedTask;
        }
    }

    private sealed class NoLock : ISetupInstanceLock
    {
        public Task<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken = default) => Task.FromResult<IAsyncDisposable>(new Lease());

        private sealed class Lease : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class MemoryResults : IApplyResultStore
    {
        private readonly Dictionary<string, SetupApplyResult> _items = [];

        public Task<SetupApplyResult?> TryGetAsync(string planHash, CancellationToken cancellationToken = default) => Task.FromResult(_items.GetValueOrDefault(planHash));

        public Task SaveAsync(string planHash, SetupApplyResult result, CancellationToken cancellationToken = default)
        {
            _items[planHash] = result;
            return Task.CompletedTask;
        }
    }

    private sealed class StopLifetime : IHostApplicationLifetime
    {
        public bool Stopped { get; private set; }

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() => Stopped = true;
    }

    private abstract class OkStepBase : ISetupStep
    {
        public int Runs { get; protected set; }

        public abstract string Id { get; }

        public virtual IReadOnlyCollection<string> Covers => [.. SetupPlanSections.All.Select(section => section.Id)];

        public int Order => 1;

        public abstract Task<SetupStepResult> ApplyAsync(SetupPlan plan, SetupApplyContext context, CancellationToken cancellationToken);
    }

    private sealed class OkStep : OkStepBase
    {
        public override string Id => "test.step";

        public override Task<SetupStepResult> ApplyAsync(SetupPlan plan, SetupApplyContext context, CancellationToken cancellationToken)
        {
            Runs++;
            return Task.FromResult(SetupStepResult.Ok);
        }
    }

    private sealed class FailingStep : OkStepBase
    {
        public override string Id => "test.fail";

        public override Task<SetupStepResult> ApplyAsync(SetupPlan plan, SetupApplyContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new SetupStepResult(false, "boom"));
    }

    private sealed class NoCoverStep : OkStepBase
    {
        public override string Id => "test.nocover";

        public override IReadOnlyCollection<string> Covers => [];

        public override Task<SetupStepResult> ApplyAsync(SetupPlan plan, SetupApplyContext context, CancellationToken cancellationToken) =>
            Task.FromResult(SetupStepResult.Ok);
    }

    [Fact]
    public async Task A_plan_file_is_applied_with_no_bootstrap_code_and_audited_as_the_file_door()
    {
        var step = new OkStep();
        var rig = new Rig(step);

        await rig.Service(Write(_plan), setupOnly: false).StartAsync(TestContext.Current.CancellationToken);

        var applied = Assert.Single(rig.Audit, r => r.Kind == SetupApplier.AppliedKind);
        Assert.Equal(SetupPlanOnStartService.OperatorIdentity, applied.Actor);
        Assert.Contains("file", applied.Payload, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, step.Runs);
        Assert.Equal(0, rig.Outcome.ExitCode);
        Assert.False(rig.Lifetime.Stopped);
        Assert.Equal(PlanHash(), rig.Outcome.PlanHash);
    }

    [Fact]
    public async Task With_setup_only_a_successful_apply_stops_the_host_with_exit_zero()
    {
        var rig = new Rig();

        await rig.Service(Write(_plan), setupOnly: true).StartAsync(TestContext.Current.CancellationToken);

        Assert.True(rig.Lifetime.Stopped);
        Assert.True(rig.Outcome.StopRequested);
        Assert.Equal(0, rig.Outcome.ExitCode);
    }

    [Fact]
    public async Task A_plan_already_applied_is_not_applied_again_on_restart()
    {
        var step = new OkStep();
        var rig = new Rig(step);
        var path = Write(_plan);

        await rig.Service(path, setupOnly: false).StartAsync(TestContext.Current.CancellationToken);
        await rig.Service(path, setupOnly: false).StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, step.Runs);
        Assert.Single(rig.Audit, r => r.Kind == SetupApplier.AppliedKind);
    }

    [Fact]
    public async Task With_setup_only_an_invalid_plan_exits_with_code_1_and_writes_nothing()
    {
        var rig = new Rig();

        await rig.Service(Write("{ not json"), setupOnly: true).StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal((int)SetupExitCode.InvalidInput, rig.Outcome.ExitCode);
        Assert.True(rig.Lifetime.Stopped);
        Assert.Empty(rig.Audit);
    }

    [Fact]
    public async Task With_setup_only_a_missing_file_exits_with_code_1()
    {
        var rig = new Rig();

        await rig.Service(Path.Combine(_dir, "absent.json"), setupOnly: true).StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, rig.Outcome.ExitCode);
        Assert.True(rig.Lifetime.Stopped);
    }

    [Fact]
    public async Task With_setup_only_a_failing_step_exits_with_code_3()
    {
        var rig = new Rig(new FailingStep());

        await rig.Service(Write(_plan), setupOnly: true).StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal((int)SetupExitCode.ApplyFailedRolledBack, rig.Outcome.ExitCode);
        Assert.True(rig.Lifetime.Stopped);
    }

    [Fact]
    public async Task With_setup_only_a_held_plan_exits_with_code_2_and_is_not_stored()
    {
        var rig = new Rig(new NoCoverStep());

        await rig.Service(Write(_plan), setupOnly: true).StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal((int)SetupExitCode.Held, rig.Outcome.ExitCode);
        Assert.Null(await rig.Results.TryGetAsync(PlanHash(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Without_setup_only_a_failure_keeps_serving_and_asks_for_no_exit_code()
    {
        var rig = new Rig(new FailingStep());

        await rig.Service(Write(_plan), setupOnly: false).StartAsync(TestContext.Current.CancellationToken);

        Assert.False(rig.Lifetime.Stopped);
        Assert.Equal(0, rig.Outcome.ExitCode);
    }

    [Fact]
    public async Task Without_a_plan_path_nothing_happens()
    {
        var rig = new Rig();

        await rig.Service(null, setupOnly: true).StartAsync(TestContext.Current.CancellationToken);

        Assert.False(rig.Lifetime.Stopped);
        Assert.False(rig.Outcome.StopRequested);
        Assert.Empty(rig.Audit);
    }

    [Fact]
    public void Options_read_the_flag_and_the_environment_form_and_the_flag_wins()
    {
        var flag = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["setup-plan"] = "/a.json",
            ["BAZAAR_SETUP_PLAN"] = "/b.json",
            ["setup-only"] = "true",
        }).Build();
        var env = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["BAZAAR_SETUP_PLAN"] = "/b.json",
            ["BAZAAR_SETUP_ONLY"] = "1",
        }).Build();

        Assert.Equal(new SetupPlanStartOptions("/a.json", true), SetupPlanStartOptions.FromConfiguration(flag));
        Assert.Equal(new SetupPlanStartOptions("/b.json", true), SetupPlanStartOptions.FromConfiguration(env));
        Assert.Equal(new SetupPlanStartOptions(null, false), SetupPlanStartOptions.FromConfiguration(new ConfigurationBuilder().Build()));
    }

    [Fact]
    public void The_component_registers_the_start_apply_before_the_bootstrap_code_issuer()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IAuditLog>(new ListAuditLog([]));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["setup-plan"] = "/x.json" }).Build();
        new SettingsComponent().AddServices(services, configuration);
        new SetupComponent([new OkStep()]).AddServices(services, configuration);

        var hosted = services.Where(d => d.ServiceType == typeof(IHostedService)).Select(d => d.ImplementationType).ToList();

        var plan = hosted.IndexOf(typeof(SetupPlanOnStartService));
        Assert.True(plan >= 0);
        Assert.True(plan < hosted.IndexOf(typeof(SetupBootstrapCodeIssuer)));
        using var provider = services.BuildServiceProvider();
        Assert.Equal("/x.json", provider.GetRequiredService<SetupPlanStartOptions>().PlanPath);
    }
}
