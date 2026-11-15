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
        _agents = new PostgresAgentRegistry(_db.DataSource, _auditLog, TimeProvider.System);
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
        await _agents.TransitionAsync(_alpha, AgentState.Quarantined, null, CancellationToken.None);
        var again = await _agents.RegisterAsync(_alpha, CancellationToken.None);

        Assert.Equal(AgentState.Active, first.State);
        Assert.Equal(AgentState.Quarantined, again.State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _agents.TransitionAsync(new AgentId("nobody"), AgentState.Revoked, null, CancellationToken.None));
    }

    [Fact]
    public async Task QuarantineNeedsAnOwnerReleaseToLeave()
    {
        await _agents.RegisterAsync(_alpha, CancellationToken.None);
        await _agents.TransitionAsync(_alpha, AgentState.Quarantined, null, CancellationToken.None);

        await Assert.ThrowsAsync<AgentTransitionException>(() => _agents.TransitionAsync(_alpha, AgentState.Active, null, CancellationToken.None));
        Assert.Equal(AgentState.Quarantined, (await _agents.GetAsync(_alpha, CancellationToken.None))!.State);

        var released = await _agents.TransitionAsync(_alpha, AgentState.Active, new OwnerRelease("dom", "reviewed the incident"), CancellationToken.None);

        Assert.Equal(AgentState.Active, released.State);
        Assert.Equal(1, await ScalarIntAsync("SELECT count(*)::int FROM audit_log WHERE kind = 'secrets.agent.transition' AND payload->>'from' = 'Quarantined' AND payload->>'to' = 'Active' AND actor = 'dom'"));
    }

    [Fact]
    public async Task RevokedIsTerminalAndSameStateIsANoOp()
    {
        await _agents.RegisterAsync(_alpha, CancellationToken.None);
        await _agents.TransitionAsync(_alpha, AgentState.Revoked, null, CancellationToken.None);
        var transitionsBefore = await ScalarIntAsync("SELECT count(*)::int FROM audit_log WHERE kind = 'secrets.agent.transition'");

        await Assert.ThrowsAsync<AgentTransitionException>(() => _agents.TransitionAsync(_alpha, AgentState.Active, new OwnerRelease("dom", "try to revive"), CancellationToken.None));
        await Assert.ThrowsAsync<AgentTransitionException>(() => _agents.TransitionAsync(_alpha, AgentState.Quarantined, null, CancellationToken.None));
        var same = await _agents.TransitionAsync(_alpha, AgentState.Revoked, null, CancellationToken.None);

        Assert.Equal(AgentState.Revoked, same.State);
        Assert.Equal(transitionsBefore, await ScalarIntAsync("SELECT count(*)::int FROM audit_log WHERE kind = 'secrets.agent.transition'"));
    }

    [Fact]
    public async Task EveryAllowedTransitionIsAuditedWithItsEndpoints()
    {
        await _agents.RegisterAsync(_alpha, CancellationToken.None);
        await _agents.TransitionAsync(_alpha, AgentState.Quarantined, null, CancellationToken.None);
        await _agents.TransitionAsync(_alpha, AgentState.Revoked, null, CancellationToken.None);

        Assert.Equal(2, await ScalarIntAsync("SELECT count(*)::int FROM audit_log WHERE kind = 'secrets.agent.transition' AND payload->>'agent' = 'alpha'"));
        Assert.Equal(1, await ScalarIntAsync("SELECT count(*)::int FROM audit_log WHERE payload->>'from' = 'Quarantined' AND payload->>'to' = 'Revoked'"));
    }

    [Fact]
    public async Task IssuesAnOpaqueTokenOnlyForAnActiveAgent()
    {
        await _agents.RegisterAsync(_alpha, CancellationToken.None);
        var issuer = new PostgresAgentCredentialIssuer(_db.DataSource, _auditLog, TimeProvider.System);

        var credential = await issuer.IssueAsync(_alpha, "gateway.call", TimeSpan.FromMinutes(10), CancellationToken.None);

        Assert.False(string.IsNullOrEmpty(credential.Token));
        Assert.DoesNotContain(credential.Token, credential.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, await ScalarIntAsync($"SELECT count(*)::int FROM audit_log WHERE payload::text LIKE '%{credential.Token}%'"));
        Assert.Equal(1, await ScalarIntAsync("SELECT count(*)::int FROM audit_log WHERE kind = 'secrets.credential.issued' AND payload->>'issuance' = '" + credential.IssuanceId + "'"));
    }

    [Fact]
    public async Task RefusesQuarantinedAndRevokedAgentsAndAuditsTheRefusal()
    {
        await _agents.RegisterAsync(_alpha, CancellationToken.None);
        var issuer = new PostgresAgentCredentialIssuer(_db.DataSource, _auditLog, TimeProvider.System);
        await _agents.TransitionAsync(_alpha, AgentState.Quarantined, null, CancellationToken.None);

        await Assert.ThrowsAsync<AgentCredentialDeniedException>(() => issuer.IssueAsync(_alpha, "gateway.call", TimeSpan.FromMinutes(10), CancellationToken.None));
        await _agents.TransitionAsync(_alpha, AgentState.Revoked, null, CancellationToken.None);
        await Assert.ThrowsAsync<AgentCredentialDeniedException>(() => issuer.IssueAsync(_alpha, "gateway.call", TimeSpan.FromMinutes(10), CancellationToken.None));

        Assert.Equal(2, await ScalarIntAsync("SELECT count(*)::int FROM audit_log WHERE kind = 'secrets.credential.denied' AND payload->>'agent' = 'alpha'"));
        Assert.Equal(0, await ScalarIntAsync("SELECT count(*)::int FROM secret_agent_credential"));
    }

    [Fact]
    public async Task RevokeIsIdempotentAndAuditedOnce()
    {
        await _agents.RegisterAsync(_alpha, CancellationToken.None);
        var issuer = new PostgresAgentCredentialIssuer(_db.DataSource, _auditLog, TimeProvider.System);
        var credential = await issuer.IssueAsync(_alpha, "gateway.call", TimeSpan.FromMinutes(10), CancellationToken.None);

        await issuer.RevokeAsync(_alpha, credential.IssuanceId, CancellationToken.None);
        await issuer.RevokeAsync(_alpha, credential.IssuanceId, CancellationToken.None);

        Assert.Equal(1, await ScalarIntAsync("SELECT count(*)::int FROM audit_log WHERE kind = 'secrets.credential.revoked'"));
        Assert.Equal(1, await ScalarIntAsync("SELECT count(*)::int FROM secret_agent_credential WHERE revoked_at IS NOT NULL"));
    }

    [Fact]
    public async Task RejectsLifetimesOutsideOneMinuteToOneDay()
    {
        await _agents.RegisterAsync(_alpha, CancellationToken.None);
        var issuer = new PostgresAgentCredentialIssuer(_db.DataSource, _auditLog, TimeProvider.System);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => issuer.IssueAsync(_alpha, "gateway.call", TimeSpan.FromSeconds(30), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => issuer.IssueAsync(_alpha, "gateway.call", TimeSpan.FromDays(2), CancellationToken.None));
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
