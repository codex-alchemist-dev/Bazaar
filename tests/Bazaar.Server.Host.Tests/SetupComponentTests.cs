using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Bazaar.Core;
using Bazaar.Server.BackupPolicy;
using Bazaar.Server.Host.Components.Settings;
using Bazaar.Server.MailPolicy;
using Bazaar.Server.Modules;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.Setup;
using Bazaar.Server.Setup.Host;
using Bazaar.Server.Setup.Http;
using Bazaar.Server.Wire.Connection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Host.Tests;

/// <summary>
/// The setup component as the host wires it (doc 45 section 1.9, doc 40). The provisioning routes are absent without the component,
/// answer 503 when a dependency is missing, refuse a call that has no bootstrap code or owner, and an accepted apply writes its audit
/// rows through the real settings and audit adapters. No database is needed: the durable stores are replaced by in-memory fakes, and
/// the Postgres stores have their own tests in Bazaar.Server.Setup.Postgres.Tests.
/// </summary>
public sealed class SetupComponentTests
{
    private const string _ownerToken = "owner-token";
    private const string _agentToken = "agent-token";
    private const string _goodCode = "good-code-0001";

    internal const string _plan = """
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

    private sealed class Rig : IAsyncDisposable
    {
        public List<AuditEntry> Audit { get; } = [];

        public bool OwnerExists { get; set; }

        public bool UseComponent { get; init; } = true;

        public Action<IServiceCollection>? Tweak { get; init; }

        public WebApplication App { get; private set; } = null!;

        public HttpClient Client { get; private set; } = null!;

        public SetupComponent Component { get; } = new([new OkStep()]);

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            var services = builder.Services;
            services.AddSingleton(TimeProvider.System);
            services.AddSingleton<IAuditLog>(new ListAuditLog(Audit));
            services.AddSingleton<IOptions<WireOptions>>(Options.Create(new WireOptions()));
            services.AddSingleton<IConnectionAuthenticator>(new TokenAuthenticator());
            // Durable stores replaced by fakes, so no database is needed. The component's TryAdd leaves them in place.
            services.AddSingleton<ISetupInstanceLock>(new NoLock());
            services.AddSingleton<IApplyResultStore>(new MemoryResults());
            services.AddSingleton<IBootstrapCodeStore>(new FixedCode());
            services.AddSingleton<ISetupOwnerDirectory>(new FlagOwner(this));
            services.AddSingleton<IBootstrapCodeAnnouncer>(new SilentAnnouncer());
            if (UseComponent)
            {
                var configuration = new ConfigurationBuilder().Build();
                new SettingsComponent().AddServices(services, configuration);
                // The keys a plan projects from modules, composed as the host does (HostComponents).
                new ModuleManagerComponent().AddServices(services, configuration);
                new BackupPolicyComponent().AddServices(services, configuration);
                new MailPolicyComponent().AddServices(services, configuration);
                Component.AddServices(services, configuration);
            }

            Tweak?.Invoke(services);
            var app = builder.Build();
            app.Use(async (context, next) =>
            {
                var remote = context.Request.Headers["X-Test-Remote"].ToString();
                context.Connection.RemoteIpAddress = IPAddress.Parse(remote.Length == 0 ? "127.0.0.1" : remote);
                await next(context);
            });
            app.MapBazaarSetup();
            await app.StartAsync(cancellationToken);
            App = app;
            Client = app.GetTestClient();
        }

        public Task<HttpResponseMessage> PostAsync(string path, string json, string? code = null, string? token = null, CancellationToken cancellationToken = default)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            if (code is not null) request.Headers.Add(SetupHttpConstants.BootstrapCodeHeader, code);
            if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return Client.SendAsync(request, cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            Client?.Dispose();
            if (App is not null)
            {
                await App.StopAsync(CancellationToken.None);
                await App.DisposeAsync();
            }
        }
    }

    private sealed class ListAuditLog(List<AuditEntry> rows) : IAuditLog
    {
        public Task AppendAsync(AuditEntry e, CancellationToken ct = default)
        {
            lock (rows) rows.Add(e);
            return Task.CompletedTask;
        }
    }

    private sealed class TokenAuthenticator : IConnectionAuthenticator
    {
        public static readonly Guid OwnerId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        public static readonly Guid AgentId = Guid.Parse("22222222-2222-2222-2222-222222222222");

        public ValueTask<AuthResult> AuthenticateAsync(HelloCredential? credential, DeviceInfo device, CancellationToken cancellationToken) =>
            ValueTask.FromResult(credential?.Value switch
            {
                _ownerToken => AuthResult.Success(new PrincipalRef(OwnerId, "owner")),
                _agentToken => AuthResult.Success(new PrincipalRef(AgentId, "agent")),
                _ => AuthResult.Fail("bad"),
            });
    }

    private sealed class NoLock : ISetupInstanceLock
    {
        public Task<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IAsyncDisposable>(new Lease());

        private sealed class Lease : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class MemoryResults : IApplyResultStore
    {
        private readonly Dictionary<string, SetupApplyResult> _items = [];

        public Task<SetupApplyResult?> TryGetAsync(string planHash, CancellationToken cancellationToken = default) =>
            Task.FromResult(_items.GetValueOrDefault(planHash));

        public Task SaveAsync(string planHash, SetupApplyResult result, CancellationToken cancellationToken = default)
        {
            _items.TryAdd(planHash, result);
            return Task.CompletedTask;
        }
    }

    private sealed class FixedCode : IBootstrapCodeStore
    {
        private bool _used;

        public Task<string> CreateAsync(CancellationToken cancellationToken = default) => Task.FromResult(_goodCode);

        public Task<bool> ConsumeAsync(string presentedCode, CancellationToken cancellationToken = default)
        {
            var ok = !_used && presentedCode == _goodCode;
            _used |= ok;
            return Task.FromResult(ok);
        }

        public Task RevokeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FlagOwner(Rig rig) : ISetupOwnerDirectory
    {
        public Task<bool> OwnerExistsAsync(CancellationToken cancellationToken = default) => Task.FromResult(rig.OwnerExists);
    }

    private sealed class SilentAnnouncer : IBootstrapCodeAnnouncer
    {
        public void Announce(string code)
        {
        }
    }

    private sealed class OkStep : ISetupStep
    {
        public string Id => "test.step";

        public IReadOnlyCollection<string> Covers => [.. SetupPlanSections.All.Select(section => section.Id)];

        public int Order => 1;

        public Task<SetupStepResult> ApplyAsync(SetupPlan plan, SetupApplyContext context, CancellationToken cancellationToken) =>
            Task.FromResult(SetupStepResult.Ok);
    }

    private static string PlanHash() => SetupPlanLoader.Load(_plan, RegistrySetupSettingCatalog.Known).PlanHash!;

    [Fact]
    public void The_manifest_is_valid_and_the_component_is_in_the_host_list()
    {
        Assert.Equal("setup", SetupComponent.ManifestDefinition.Id);
        Assert.Contains(SetupComponent.ManifestDefinition.Id, Composition.HostComponents.All.Select(c => c.Manifest.Id));
        // The kernel validates the whole host graph (ordering, dependencies); this throws if the setup entry breaks it.
        Assert.NotNull(ComponentGraph.Order(Composition.HostComponents.All.Select(c => c.Manifest)));
    }

    [Theory]
    [InlineData("/setup/status", "GET")]
    [InlineData("/setup/plans", "POST")]
    public async Task Without_the_component_the_setup_routes_do_not_exist(string path, string method)
    {
        await using var rig = new Rig { UseComponent = false };
        await rig.StartAsync(TestContext.Current.CancellationToken);

        var response = await rig.Client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(rig.Audit);
    }

    [Fact]
    public async Task With_the_component_but_a_missing_dependency_the_routes_answer_503_and_write_nothing()
    {
        await using var rig = new Rig { Tweak = s => s.RemoveAll<ISetupAccessGate>() };
        await rig.StartAsync(TestContext.Current.CancellationToken);

        var response = await rig.Client.GetAsync("/setup/status", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Empty(rig.Audit);
    }

    [Fact]
    public async Task A_call_without_a_bootstrap_code_before_the_owner_exists_is_refused_and_audited()
    {
        await using var rig = new Rig();
        await rig.StartAsync(TestContext.Current.CancellationToken);

        var none = await rig.PostAsync("/setup/plans", _plan, cancellationToken: TestContext.Current.CancellationToken);
        var wrong = await rig.PostAsync("/setup/plans", _plan, code: "not-the-code", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(2, rig.Audit.Count(r => r.Kind == SetupHttpConstants.AuditKind && r.Actor == SetupHttpConstants.RefusedActor));
        Assert.DoesNotContain(rig.Audit, r => r.Kind == SetupApplier.AppliedKind);
    }

    [Fact]
    public async Task A_valid_bootstrap_code_from_off_the_machine_is_refused()
    {
        await using var rig = new Rig();
        await rig.StartAsync(TestContext.Current.CancellationToken);
        var request = new HttpRequestMessage(HttpMethod.Post, "/setup/plans") { Content = new StringContent(_plan, Encoding.UTF8, "application/json") };
        request.Headers.Add("X-Test-Remote", "10.1.2.3");
        request.Headers.Add(SetupHttpConstants.BootstrapCodeHeader, _goodCode);

        var response = await rig.Client.SendAsync(request, TestContext.Current.CancellationToken);

        // Plaintext from a non-loopback peer is refused by the transport policy (the default wire option is off).
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task After_the_owner_exists_only_the_owner_role_is_admitted_and_the_bootstrap_code_is_dead()
    {
        await using var rig = new Rig { OwnerExists = true };
        await rig.StartAsync(TestContext.Current.CancellationToken);

        var code = await rig.PostAsync("/setup/plans", _plan, code: _goodCode, cancellationToken: TestContext.Current.CancellationToken);
        var agent = await rig.PostAsync("/setup/plans", _plan, token: _agentToken, cancellationToken: TestContext.Current.CancellationToken);
        var owner = await rig.PostAsync("/setup/plans", _plan, token: _ownerToken, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, code.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, agent.StatusCode);
        Assert.Equal(HttpStatusCode.OK, owner.StatusCode);
    }

    [Fact]
    public async Task An_accepted_apply_writes_the_applied_row_and_the_setting_row_through_the_real_adapters()
    {
        await using var rig = new Rig();
        await rig.StartAsync(TestContext.Current.CancellationToken);

        var response = await rig.PostAsync($"/setup/plans/{PlanHash()}/apply", _plan, code: _goodCode, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var applied = Assert.Single(rig.Audit, r => r.Kind == SetupApplier.AppliedKind);
        Assert.Equal(SetupHttpConstants.BootstrapActor, applied.Actor);
        // ISetupSettingAudit -> SettingAuditSinkSetupAudit -> ISettingAuditSink -> IAuditLog: one setting.change row per setting the plan
        // sets (the plan's settings map, the network block and the release channel).
        var settings = rig.Audit.Where(r => r.Kind == AuditLogSettingSink.Kind).ToList();
        Assert.Equal(4, settings.Count);
        // sets (the plan's settings map, the network block, the release channel, the security update policy, backups, email, the merge credential kind and reference, the default preset and the company source).
        var settings = rig.Audit.Where(r => r.Kind == AuditLogSettingSink.Kind).ToList();
        Assert.Equal(14, settings.Count);
        Assert.Contains(settings, r => r.Payload.Contains("host.shutdown_timeout_seconds", StringComparison.Ordinal));
        Assert.All(settings, r => Assert.Equal(SetupHttpConstants.BootstrapActor, r.Actor));
        Assert.Contains(rig.Audit, r => r.Kind == SetupHttpConstants.AuditKind);
    }

    [Fact]
    public async Task The_component_registers_its_adapters_and_the_issuer_hosted_service()
    {
        await using var rig = new Rig();
        await rig.StartAsync(TestContext.Current.CancellationToken);
        var sp = rig.App.Services;

        Assert.IsType<RegistrySetupSettingCatalog>(sp.GetRequiredService<ISetupSettingCatalog>());
        Assert.IsType<SettingAuditSinkSetupAudit>(sp.GetRequiredService<ISetupSettingAudit>());
        Assert.IsType<ConnectionSetupCallerAuthenticator>(sp.GetRequiredService<ISetupCallerAuthenticator>());
        Assert.IsType<WireSetupTransportPolicy>(sp.GetRequiredService<ISetupTransportPolicy>());
        Assert.Contains(sp.GetServices<IHostedService>(), s => s is SetupBootstrapCodeIssuer);
    }
}
