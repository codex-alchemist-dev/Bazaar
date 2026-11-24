using System.Net;
using System.Text;
using Bazaar.Server.Host.Composition;
using Bazaar.Server.Host.Status;
using Bazaar.Server.Http;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.Setup;
using Bazaar.Server.Setup.Host;
using Bazaar.Server.Setup.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Bazaar.Server.Host.Tests;

/// <summary>
/// The provisioning routes on the real host graph (<c>AddBazaarHostApplication</c>, the same call and the same mapping order as
/// Program.cs), not on a hand-built rig. Regression guard: the host once answered 404 on <c>/setup/*</c> because it did not ship the
/// setup component. Only the durable Postgres stores are replaced by in-memory fakes, and the host's hosted services are removed so
/// no database or port is touched. Doc 45 sections 1.7 and 1.9, doc 40.
/// </summary>
public sealed class SetupHostEndpointsTests
{
    private const string _code = "host-test-code-0001";

    private static async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var services = builder.Services;
        services.AddSingleton<IAuditLog>(new MemoryAudit());
        services.AddSingleton<ISetupInstanceLock>(new NoLock());
        services.AddSingleton<IApplyResultStore>(new MemoryResults());
        services.AddSingleton<IBootstrapCodeStore>(new FixedCode());
        services.AddSingleton<ISetupOwnerDirectory>(new NoOwner());
        services.AddSingleton<IBootstrapCodeAnnouncer>(new SilentAnnouncer());
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Bazaar:Host:DataRoot"] = Path.Combine(Path.GetTempPath(), "bazaar-host-test-" + Guid.NewGuid().ToString("N")),
            ["Bazaar:Host:DatabaseConnectionReference"] = "env:BAZAAR_SETUP_HOST_TEST_DB",
        });
        Environment.SetEnvironmentVariable("BAZAAR_SETUP_HOST_TEST_DB", "Host=127.0.0.1;Port=1;Database=unused");
        services.AddBazaarHostApplication(builder.Configuration);
        services.RemoveAll<IHostedService>();

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Loopback;
            await next(context);
        });
        app.UseBazaarGetOnly();
        app.MapBazaarHttpSurface();
        app.MapBazaarSetup();
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    private static HttpRequestMessage Post(string path, string? code)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(SetupComponentTests._plan, Encoding.UTF8, "application/json") };
        if (code is not null)
        {
            request.Headers.Add(SetupHttpConstants.BootstrapCodeHeader, code);
        }

        return request;
    }

    [Fact]
    public async Task The_real_host_serves_the_setup_routes_and_keeps_the_bootstrap_guard()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();
        var ct = TestContext.Current.CancellationToken;
        var hash = SetupPlanLoader.Load(SetupComponentTests._plan, RegistrySetupSettingCatalog.Known).PlanHash!;

        // No owner yet and no code: refused, never 404 and never an open route.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Post("/setup/plans", null), ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Post("/setup/plans", "wrong"), ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Post($"/setup/plans/{hash}/apply", null), ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/setup/status", ct)).StatusCode);

        // Wrong method is a 405 from the router, which also proves the route exists.
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.GetAsync("/setup/plans", ct)).StatusCode);

        // A valid plan is validated (the dry run writes nothing).
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Post("/setup/plans", _code), ct)).StatusCode);
    }

    [Fact]
    public async Task An_apply_with_an_unknown_plan_hash_is_refused_with_the_documented_error()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();
        var ct = TestContext.Current.CancellationToken;

        var malformed = await client.SendAsync(Post("/setup/plans/not-a-hash/apply", _code), ct);
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Contains("bad_hash", await malformed.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);

        var unknown = await client.SendAsync(Post($"/setup/plans/{new string('0', 64)}/apply", _code), ct);
        Assert.Equal(HttpStatusCode.Conflict, unknown.StatusCode);
        Assert.Contains("hash_mismatch", await unknown.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
    }

    private sealed class MemoryAudit : IAuditLog
    {
        public Task AppendAsync(AuditEntry e, CancellationToken ct = default) => Task.CompletedTask;
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
        public Task<SetupApplyResult?> TryGetAsync(string planHash, CancellationToken cancellationToken = default) => Task.FromResult<SetupApplyResult?>(null);

        public Task SaveAsync(string planHash, SetupApplyResult result, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FixedCode : IBootstrapCodeStore
    {
        public Task<string> CreateAsync(CancellationToken cancellationToken = default) => Task.FromResult(_code);

        public Task<bool> ConsumeAsync(string presentedCode, CancellationToken cancellationToken = default) => Task.FromResult(presentedCode == _code);

        public Task RevokeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NoOwner : ISetupOwnerDirectory
    {
        public Task<bool> OwnerExistsAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private sealed class SilentAnnouncer : IBootstrapCodeAnnouncer
    {
        public void Announce(string code)
        {
        }
    }
}
