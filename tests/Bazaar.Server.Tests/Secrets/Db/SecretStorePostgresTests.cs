using System.Security.Cryptography;
using Bazaar.Secrets;
using Bazaar.Server.Secrets;
using Bazaar.Server.Secrets.Db;
using Bazaar.Server.Security.Audit.Db;
using Npgsql;

namespace Bazaar.Server.Tests.Secrets.Db;

/// <summary>The real secret store over PostgreSQL: the migration, both repositories, and the audit log.</summary>
public sealed class SecretStorePostgresTests : IAsyncLifetime
{
    private static readonly AgentId _alpha = new("alpha");
    private static readonly SecretPurpose _gateway = new("gateway.call");

    private SecretsTestDb _db = null!;
    private PostgresAuditLog _auditLog = null!;
    private PostgresSecretRecordRepository _records = null!;
    private PostgresAgentRegistry _agents = null!;

    public async ValueTask InitializeAsync()
    {
        _db = await SecretsTestDb.CreateAsync(CancellationToken.None);
        _auditLog = new PostgresAuditLog(_db.DataSource);
        _records = new PostgresSecretRecordRepository(_db.DataSource);
        _agents = new PostgresAgentRegistry(_db.DataSource, TimeProvider.System);
    }

    public async ValueTask DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task PutThenGetReturnsTheNewestVersion()
    {
        var store = Store(new StaticSecretPolicy([new SecretGrant("gateway", "installation", null, SecretPurposes.Manage), new SecretGrant("gateway", "installation", null, _gateway)]));

        await store.PutAsync(new HostCaller("gateway"), SecretScope.Installation, new SecretName("api-key"), SecretValue.FromText("first"), CancellationToken.None);
        await store.PutAsync(new HostCaller("gateway"), SecretScope.Installation, new SecretName("api-key"), SecretValue.FromText("second"), CancellationToken.None);
        using var value = await store.GetAsync(new HostCaller("gateway"), SecretScope.Installation, new SecretName("api-key"), _gateway, CancellationToken.None);

        Assert.Equal("second", System.Text.Encoding.UTF8.GetString(value.Bytes));
        Assert.Equal(2, await ScalarIntAsync("SELECT version FROM secret_record WHERE scope_key = 'installation' AND name = 'api-key'"));
    }

    [Fact]
    public async Task CryptoEraseMakesTheScopeUnreadable()
    {
        var store = Store(new StaticSecretPolicy([new SecretGrant("gateway", "agent:alpha", null, SecretPurposes.Manage), new SecretGrant("gateway", "agent:alpha", null, _gateway)]));
        await _agents.RegisterAsync(_alpha, CancellationToken.None);
        await store.PutAsync(new HostCaller("gateway"), SecretScope.ForAgent(_alpha), new SecretName("token"), SecretValue.FromText("s3cret"), CancellationToken.None);

        await store.CryptoEraseAsync(new HostCaller("gateway"), _alpha, CancellationToken.None);

        Assert.Equal(0, await ScalarIntAsync("SELECT count(*)::int FROM secret_record WHERE scope_key = 'agent:alpha'"));
        Assert.True(await ScalarIntAsync("SELECT count(*)::int FROM secret_scope_key WHERE scope_key = 'agent:alpha' AND destroyed_at IS NOT NULL AND wrapped_key IS NULL") == 1);
        await Assert.ThrowsAsync<SecretNotFoundException>(() => store.GetAsync(new HostCaller("gateway"), SecretScope.ForAgent(_alpha), new SecretName("token"), _gateway, CancellationToken.None));
    }

    [Fact]
    public async Task AgentCallerIsDeniedAndTheDenialIsAudited()
    {
        var store = Store(StaticSecretPolicy.DenyAll);

        await Assert.ThrowsAsync<SecretAccessDeniedException>(() => store.GetAsync(new AgentCaller(_alpha), SecretScope.Installation, new SecretName("api-key"), _gateway, CancellationToken.None));

        Assert.Equal(1, await ScalarIntAsync("SELECT count(*)::int FROM audit_log WHERE kind = 'secrets.get.denied' AND actor = 'agent:alpha'"));
    }

    [Fact]
    public async Task HostWithoutGrantIsDeniedAndAuditedWithNoValueInPayload()
    {
        var store = Store(StaticSecretPolicy.DenyAll);

        await Assert.ThrowsAsync<SecretAccessDeniedException>(() => store.PutAsync(new HostCaller("gateway"), SecretScope.Installation, new SecretName("api-key"), SecretValue.FromText("leak-me-not"), CancellationToken.None));

        Assert.Equal(0, await ScalarIntAsync("SELECT count(*)::int FROM secret_record"));
        Assert.Equal(1, await ScalarIntAsync("SELECT count(*)::int FROM audit_log WHERE kind = 'secrets.put.denied' AND payload->>'name' = 'api-key'"));
        Assert.Equal(0, await ScalarIntAsync("SELECT count(*)::int FROM audit_log WHERE payload::text LIKE '%leak-me-not%'"));
    }

    [Fact]
    public async Task RegistrationIsIdempotentAndStateChangesStick()
    {
        var first = await _agents.RegisterAsync(_alpha, CancellationToken.None);
        await _agents.SetStateAsync(_alpha, AgentState.Quarantined, CancellationToken.None);
        var again = await _agents.RegisterAsync(_alpha, CancellationToken.None);

        Assert.Equal(AgentState.Active, first.State);
        Assert.Equal(AgentState.Quarantined, again.State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _agents.SetStateAsync(new AgentId("nobody"), AgentState.Revoked, CancellationToken.None));
    }

    [Fact]
    public async Task ScopeKeyIsWrittenOnceUnderRacingInserts()
    {
        var record = new ScopeKeyRecord("installation", "kek-1", new byte[] { 1, 2, 3 }, DateTimeOffset.UtcNow, null);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => _records.TryInsertScopeKeyAsync(record, CancellationToken.None)));

        Assert.Single(results, won => won);
        Assert.Equal(1, await ScalarIntAsync("SELECT count(*)::int FROM secret_scope_key"));
    }

    private SecretStore Store(ISecretPolicy policy) =>
        new(new SoftwareKekProvider("kek-test", RandomNumberGenerator.GetBytes(32)), _records, _agents, policy, new AuditLogSecretSink(_auditLog), TimeProvider.System);

    private async Task<int> ScalarIntAsync(string sql)
    {
        await using var conn = await _db.DataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
