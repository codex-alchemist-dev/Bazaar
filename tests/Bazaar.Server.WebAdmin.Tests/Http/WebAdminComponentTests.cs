using System.Text.RegularExpressions;
using Cdax.Core;
using Bazaar.Roles.Abstractions;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.WebAdmin.Http;
using Bazaar.Server.WebAdmin.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.WebAdmin.Tests.Http;

public sealed partial class WebAdminComponentTests
{
    [GeneratedRegex("^[a-z][a-z0-9]*(-[a-z0-9]+)*$")]
    private static partial Regex Kebab();

    private static ServiceProvider Build(Dictionary<string, string?> config, Action<IServiceCollection>? extra = null)
    {
        var services = new ServiceCollection();
        extra?.Invoke(services);
        new WebAdminComponent().AddServices(services, new ConfigurationBuilder().AddInMemoryCollection(config).Build());
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Manifest_is_valid()
    {
        var m = new WebAdminComponent().Manifest;
        Assert.Equal("web-admin", m.Id);
        Assert.Matches(Kebab(), m.Id);
        Assert.Equal("0.1.0", m.Version);
        Assert.Equal(ComponentKind.Feature, m.Kind);
        Assert.Equal(["roles", "audit"], m.DependsOn);
        Assert.Equal(["feature:web-admin"], m.Provides);
    }

    [Fact]
    public void Defaults_are_registered_and_session_options_use_the_floors()
    {
        using var sp = Build([], s =>
        {
            s.AddSingleton<IPermissionChecker, NoChecker>();
            s.AddSingleton<IAuditLog, NullLog>();
        });
        Assert.Equal(AdminSessionOptions.Default, sp.GetRequiredService<AdminSessionOptions>());
        Assert.IsType<EmbeddedAssetProvider>(sp.GetRequiredService<IAdminAssetProvider>());
        Assert.NotNull(sp.GetRequiredService<IAdminSessionStore>());
        Assert.IsType<CookieAdminSessionReader>(sp.GetRequiredService<IAdminSessionReader>());
        Assert.NotNull(sp.GetRequiredService<IAdminGate>());
        Assert.NotNull(sp.GetRequiredService<IAdminRevocationCheck>());
        Assert.NotNull(sp.GetRequiredService<TimeProvider>());
        Assert.Equal("admin", AdminRateLimit.Bucket);
    }

    [Fact]
    public void Audit_recorder_is_resolvable_when_an_audit_log_exists()
    {
        using var sp = Build([], s => s.AddSingleton<IAuditLog, NullLog>());
        Assert.NotNull(sp.GetRequiredService<IAdminAudit>());
    }

    [Fact]
    public void Session_settings_may_tighten_and_never_loosen()
    {
        using var tight = Build(new() { ["webadmin:session:slidingMinutes"] = "5", ["webadmin:session:absoluteHours"] = "2" });
        Assert.Equal(new AdminSessionOptions(TimeSpan.FromMinutes(5), TimeSpan.FromHours(2)), tight.GetRequiredService<AdminSessionOptions>());

        using var loose = Build(new() { ["webadmin:session:slidingMinutes"] = "600", ["webadmin:session:absoluteHours"] = "100" });
        Assert.Equal(AdminSessionOptions.Default, loose.GetRequiredService<AdminSessionOptions>());

        using var junk = Build(new() { ["webadmin:session:slidingMinutes"] = "abc", ["webadmin:session:absoluteHours"] = "-3" });
        Assert.Equal(AdminSessionOptions.Default, junk.GetRequiredService<AdminSessionOptions>());
    }

    [Fact]
    public void Existing_registrations_win_over_the_defaults()
    {
        using var sp = Build([], s => s.AddSingleton<IAdminAssetProvider, Fakes.FakeAssets>());
        Assert.IsType<Fakes.FakeAssets>(sp.GetRequiredService<IAdminAssetProvider>());
    }

    private sealed class NoChecker : IPermissionChecker
    {
        public Task<bool> IsAllowedAsync(Guid principalId, PrincipalKind kind, Capability capability, Scope scope, CancellationToken ct = default) => Task.FromResult(false);
    }

    private sealed class NullLog : IAuditLog
    {
        public Task AppendAsync(AuditEntry e, CancellationToken ct = default) => Task.CompletedTask;
    }
}
