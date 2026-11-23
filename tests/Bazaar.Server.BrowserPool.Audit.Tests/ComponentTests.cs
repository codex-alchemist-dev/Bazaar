using Bazaar.Core;
using Bazaar.Server.BrowserPool.Abstractions;
using Bazaar.Server.Security.Audit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.BrowserPool.Audit.Tests;

public class ComponentTests
{
    private sealed class RecordingLog : IAuditLog
    {
        public List<AuditEntry> Entries { get; } = [];

        public Task AppendAsync(AuditEntry e, CancellationToken ct = default)
        {
            Entries.Add(e);
            return Task.CompletedTask;
        }
    }

    private static ServiceProvider Build(bool auditFirst, Action<IServiceCollection>? host = null)
    {
        var services = new ServiceCollection();
        IConfiguration config = new ConfigurationBuilder().Build();
        if (auditFirst)
        {
            new BrowserPoolAuditComponent().AddServices(services, config);
            new BrowserPoolComponent().AddServices(services, config);
        }
        else
        {
            new BrowserPoolComponent().AddServices(services, config);
            new BrowserPoolAuditComponent().AddServices(services, config);
        }

        host?.Invoke(services);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Manifest_is_as_designed()
    {
        ComponentManifest m = new BrowserPoolAuditComponent().Manifest;

        Assert.Equal("browser-pool-audit", m.Id);
        Assert.Equal("0.1.0", m.Version);
        Assert.Equal(ComponentKind.Adapter, m.Kind);
        Assert.Equal(["browser-pool"], m.DependsOn);
        Assert.Equal(["browser-pool:audit"], m.Provides);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_real_sink_replaces_the_no_op_default_in_either_order(bool auditFirst)
    {
        using ServiceProvider sp = Build(auditFirst, s => s.AddSingleton<IAuditLog>(new RecordingLog()));

        Assert.IsType<AuditLogPoolAuditSink>(sp.GetRequiredService<IPoolAuditSink>());
        Assert.Single(sp.GetServices<IPoolAuditSink>());
    }

    [Fact]
    public void A_later_host_registration_still_wins()
    {
        var custom = new NoOpPoolAuditSink();

        using ServiceProvider sp = Build(false, s => s.AddSingleton<IPoolAuditSink>(custom));

        Assert.Same(custom, sp.GetRequiredService<IPoolAuditSink>());
    }

    [Fact]
    public async Task A_refused_open_through_the_real_pool_writes_a_refusal_row()
    {
        var log = new RecordingLog();
        using ServiceProvider sp = Build(true, s => s.AddSingleton<IAuditLog>(log));

        var ex = await Assert.ThrowsAsync<BrowserPoolException>(() =>
            sp.GetRequiredService<IBrowserPool>().OpenAsync(new PoolSessionRequest("agent-1", StartUrl: "https://example.test/secret-path?q=1"), TestContext.Current.CancellationToken));

        Assert.Equal(PoolFailure.AgentBlocked, ex.Failure);
        AuditEntry row = Assert.Single(log.Entries);
        Assert.Equal(AuditLogPoolAuditSink.RefusedKind, row.Kind);
        Assert.Contains("AgentBlocked", row.Payload, StringComparison.Ordinal);
        Assert.DoesNotContain("example.test", row.Payload, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-path", row.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_an_audit_log_the_pool_still_refuses_and_nothing_throws()
    {
        using ServiceProvider sp = Build(true);

        var ex = await Assert.ThrowsAsync<BrowserPoolException>(() =>
            sp.GetRequiredService<IBrowserPool>().OpenAsync(new PoolSessionRequest("agent-1"), TestContext.Current.CancellationToken));

        Assert.Equal(PoolFailure.AgentBlocked, ex.Failure);
        Assert.Equal(1, ((AuditLogPoolAuditSink)sp.GetRequiredService<IPoolAuditSink>()).FailedWrites);
    }

    [Fact]
    public void Bad_options_fail_at_start()
    {
        var services = new ServiceCollection();
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Bazaar:BrowserPoolAudit:WriteTimeoutSeconds"] = "0" }).Build();

        Assert.Throws<InvalidOperationException>(() => new BrowserPoolAuditComponent().AddServices(services, config));
    }
}
