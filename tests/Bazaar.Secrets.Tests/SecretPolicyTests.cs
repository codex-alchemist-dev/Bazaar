using System.Security.Cryptography;
using System.Text;
using Bazaar.Secrets.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Secrets.Tests;

public sealed class SecretPolicyTests : IDisposable
{
    private static readonly HostCaller _gateway = new("gateway");
    private static readonly SecretPurpose _callPurpose = new("gateway.call");
    private static readonly SecretName _providerKey = new("anthropic.key");

    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-11-15T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
    private readonly InMemorySecretRecordRepository _records = new();
    private readonly InMemoryAgentRegistry _agents;
    private readonly InMemorySecretAuditSink _audit = new();
    private readonly SoftwareKekProvider _kek = new("kek-policy", RandomNumberGenerator.GetBytes(32));

    public SecretPolicyTests()
    {
        _agents = new InMemoryAgentRegistry(_time);
    }

    public void Dispose() => _kek.Dispose();

    /// <summary>A store over the shared repository, with the given policy. Stores built this way see the same data.</summary>
    private SecretStore StoreWith(ISecretPolicy policy) => new(_kek, _records, _agents, policy, _audit, _time);

    /// <summary>A writer that may manage secrets, used to seed data before the reader under test runs.</summary>
    private SecretStore Writer() => StoreWith(new StaticSecretPolicy(
    [
        new SecretGrant("gateway", SecretScope.Installation.Key, null, SecretPurposes.Manage),
    ]));

    [Fact]
    public async Task EmptyPolicyDeniesEveryHostOperation()
    {
        var store = StoreWith(StaticSecretPolicy.DenyAll);

        var put = await Assert.ThrowsAsync<SecretAccessDeniedException>(() =>
            store.PutAsync(_gateway, SecretScope.Installation, _providerKey, SecretValue.FromText("x"), CancellationToken.None));
        var get = await Assert.ThrowsAsync<SecretAccessDeniedException>(() =>
            store.GetAsync(_gateway, SecretScope.Installation, _providerKey, _callPurpose, CancellationToken.None));

        Assert.Contains("is not granted", put.Reason, StringComparison.Ordinal);
        Assert.Contains("is not granted", get.Reason, StringComparison.Ordinal);
        Assert.All(_audit.Events, e => Assert.Equal(SecretAuditOutcome.Denied, e.Outcome));
        Assert.Equal(2, _audit.Events.Count);
    }

    [Fact]
    public async Task GrantedPurposeAllowsReadButNotOtherPurposesOrWrites()
    {
        await Writer().PutAsync(_gateway, SecretScope.Installation, _providerKey, SecretValue.FromText("sk-1"), CancellationToken.None);
        var reader = StoreWith(new StaticSecretPolicy(
        [
            new SecretGrant("gateway", SecretScope.Installation.Key, null, _callPurpose),
        ]));

        using var value = await reader.GetAsync(_gateway, SecretScope.Installation, _providerKey, _callPurpose, CancellationToken.None);
        var otherPurpose = await Assert.ThrowsAsync<SecretAccessDeniedException>(() =>
            reader.GetAsync(_gateway, SecretScope.Installation, _providerKey, new SecretPurpose("billing.report"), CancellationToken.None));
        var write = await Assert.ThrowsAsync<SecretAccessDeniedException>(() =>
            reader.PutAsync(_gateway, SecretScope.Installation, _providerKey, SecretValue.FromText("sk-2"), CancellationToken.None));

        Assert.Equal("sk-1", Encoding.UTF8.GetString(value.Bytes));
        Assert.Contains("billing.report", otherPurpose.Reason, StringComparison.Ordinal);
        Assert.Contains("secrets.manage", write.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GrantForOneComponentDoesNotCoverAnother()
    {
        await Writer().PutAsync(_gateway, SecretScope.Installation, _providerKey, SecretValue.FromText("sk-1"), CancellationToken.None);
        var store = StoreWith(new StaticSecretPolicy(
        [
            new SecretGrant("gateway", SecretScope.Installation.Key, null, _callPurpose),
        ]));

        var denied = await Assert.ThrowsAsync<SecretAccessDeniedException>(() =>
            store.GetAsync(new HostCaller("git-provider"), SecretScope.Installation, _providerKey, _callPurpose, CancellationToken.None));

        Assert.Contains("'git-provider'", denied.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NameScopedGrantCoversOnlyThatSecret()
    {
        await Writer().PutAsync(_gateway, SecretScope.Installation, _providerKey, SecretValue.FromText("sk-1"), CancellationToken.None);
        await Writer().PutAsync(_gateway, SecretScope.Installation, new SecretName("github.app"), SecretValue.FromText("pem"), CancellationToken.None);
        var store = StoreWith(new StaticSecretPolicy(
        [
            new SecretGrant("gateway", SecretScope.Installation.Key, _providerKey, _callPurpose),
        ]));

        using var allowed = await store.GetAsync(_gateway, SecretScope.Installation, _providerKey, _callPurpose, CancellationToken.None);
        var denied = await Assert.ThrowsAsync<SecretAccessDeniedException>(() =>
            store.GetAsync(_gateway, SecretScope.Installation, new SecretName("github.app"), _callPurpose, CancellationToken.None));

        Assert.Equal("sk-1", Encoding.UTF8.GetString(allowed.Bytes));
        Assert.Contains("not granted", denied.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AgentScopeGrantDoesNotCoverTheInstallation()
    {
        await _agents.RegisterAsync(new AgentId("alpha"), CancellationToken.None);
        await Writer().PutAsync(_gateway, SecretScope.Installation, _providerKey, SecretValue.FromText("sk-1"), CancellationToken.None);
        var store = StoreWith(new StaticSecretPolicy(
        [
            new SecretGrant("gateway", SecretScope.ForAgent(new AgentId("alpha")).Key, null, _callPurpose),
        ]));

        await Assert.ThrowsAsync<SecretAccessDeniedException>(() =>
            store.GetAsync(_gateway, SecretScope.Installation, _providerKey, _callPurpose, CancellationToken.None));
    }

    [Fact]
    public async Task CryptoEraseNeedsTheManageGrant()
    {
        await _agents.RegisterAsync(new AgentId("alpha"), CancellationToken.None);
        var reader = StoreWith(new StaticSecretPolicy(
        [
            new SecretGrant("gateway", SecretScope.ForAgent(new AgentId("alpha")).Key, null, _callPurpose),
        ]));

        var denied = await Assert.ThrowsAsync<SecretAccessDeniedException>(() =>
            reader.CryptoEraseAsync(_gateway, new AgentId("alpha"), CancellationToken.None));

        Assert.Contains("secrets.manage", denied.Reason, StringComparison.Ordinal);
        Assert.Contains(_audit.Events, e => e.Action == SecretAuditAction.CryptoErase && e.Outcome == SecretAuditOutcome.Denied);
    }

    [Fact]
    public void GrantsAreImmutableRecordsWithValueEquality()
    {
        var a = new SecretGrant("gateway", "installation", null, _callPurpose);
        var b = new SecretGrant("gateway", "installation", null, new SecretPurpose("gateway.call"));

        Assert.Equal(a, b);
    }
}
