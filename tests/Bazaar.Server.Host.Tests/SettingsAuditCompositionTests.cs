using System.Text.Json;
using Bazaar.Server.Host.Composition;
using Bazaar.Server.Hosting;
using Bazaar.Server.Secrets;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.Security.Audit.Db;
using Bazaar.Server.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Host.Tests;

public sealed class SettingsAuditCompositionTests : IDisposable
{
    private const string _envName = "BAZAAR_TEST_SETTINGS_AUDIT_DB";
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), "bazaar-settings-audit-" + Guid.NewGuid().ToString("N"));

    public SettingsAuditCompositionTests()
    {
        Environment.SetEnvironmentVariable(_envName, "Host=localhost;Database=x");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(_envName, null);
    }

    private ServiceProvider Build()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Bazaar:Host:DataRoot"] = _dataRoot,
            ["Bazaar:Host:DatabaseConnectionReference"] = "env:" + _envName,
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBazaarHostOptions(configuration);
        services.AddBazaarHostRuntime(configuration);
        services.AddBazaarSecrets();
        services.AddBazaarSettings();
        return services.BuildServiceProvider(validateScopes: true);
    }

    [Fact]
    public async Task Audit_log_resolves_to_the_postgres_implementation()
    {
        await using var sp = Build();
        Assert.IsType<PostgresAuditLog>(sp.GetRequiredService<IAuditLog>());
    }

    [Fact]
    public async Task Settings_store_and_registry_resolve()
    {
        await using var sp = Build();
        Assert.IsType<NpgsqlSettingStore>(sp.GetRequiredService<ISettingStore>());
        Assert.Same(SettingRegistry.Host, sp.GetRequiredService<SettingRegistry>());
        Assert.IsType<AuditLogSettingSink>(sp.GetRequiredService<ISettingAuditSink>());
    }

    [Fact]
    public async Task Setting_change_is_written_to_the_audit_log()
    {
        var log = new FakeAuditLog();
        var clock = new FixedClock(new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero));
        var sink = new AuditLogSettingSink(log, clock);

        await sink.AppendAsync(new SettingChangeAudit(
            "host.example",
            SettingScope.Install,
            "",
            "owner",
            SettingWriteOutcome.PendingApproval,
            OldValueJson: "1",
            NewValueJson: "not json",
            Reason: "needs approval"),
            TestContext.Current.CancellationToken);

        var entry = Assert.Single(log.Entries);
        Assert.Equal(clock.Now, entry.Ts);
        Assert.Equal("owner", entry.Actor);
        Assert.Equal("setting.change", entry.Kind);

        using var payload = JsonDocument.Parse(entry.Payload);
        var root = payload.RootElement;
        Assert.Equal("host.example", root.GetProperty("key").GetString());
        Assert.Equal("install", root.GetProperty("scope").GetString());
        Assert.Equal("", root.GetProperty("scopeId").GetString());
        Assert.Equal("PendingApproval", root.GetProperty("outcome").GetString());
        Assert.Equal("1", root.GetProperty("oldValueJson").GetString());
        Assert.Equal("not json", root.GetProperty("newValueJson").GetString());
        Assert.Equal("needs approval", root.GetProperty("reason").GetString());
    }

    private sealed class FakeAuditLog : IAuditLog
    {
        private readonly List<AuditEntry> _entries = [];

        public IReadOnlyList<AuditEntry> Entries => _entries;

        public Task AppendAsync(AuditEntry e, CancellationToken ct = default)
        {
            _entries.Add(e);
            return Task.CompletedTask;
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
