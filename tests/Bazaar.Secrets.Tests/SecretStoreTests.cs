using System.Security.Cryptography;
using System.Text;
using Bazaar.Secrets.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Secrets.Tests;

public sealed class SecretStoreTests : IDisposable
{
    private static readonly HostCaller Gateway = new("gateway");
    private static readonly SecretScope Installation = SecretScope.Installation;
    private static readonly SecretName ProviderKey = new("anthropic.key");
    private static readonly SecretPurpose CallPurpose = new("gateway.call");
    private static readonly AgentId Alpha = new("alpha");
    private static readonly AgentId Beta = new("beta");

    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-11-15T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
    private readonly InMemorySecretRecordRepository _records = new();
    private readonly InMemoryAgentRegistry _agents;
    private readonly InMemorySecretAuditSink _audit = new();
    private readonly SoftwareKekProvider _kek = new("kek-2026-11", RandomNumberGenerator.GetBytes(32));
    private readonly SecretStore _store;

    public SecretStoreTests()
    {
        _agents = new InMemoryAgentRegistry(_time);
        _store = new SecretStore(_kek, _records, _agents, _audit, _time);
    }

    public void Dispose() => _kek.Dispose();

    [Fact]
    public async Task HostRoundTripReturnsTheStoredValue()
    {
        await _store.PutAsync(Gateway, Installation, ProviderKey, SecretValue.FromText("sk-test-123"), CancellationToken.None);

        using var read = await _store.GetAsync(Gateway, Installation, ProviderKey, CallPurpose, CancellationToken.None);

        Assert.Equal("sk-test-123", Encoding.UTF8.GetString(read.Bytes));
    }

    [Fact]
    public async Task AgentCallersAreDeniedOnEveryOperation()
    {
        var agent = new AgentCaller(Alpha);
        await _agents.RegisterAsync(Alpha, CancellationToken.None);
        var scope = SecretScope.ForAgent(Alpha);

        var put = await Assert.ThrowsAsync<SecretAccessDeniedException>(() =>
            _store.PutAsync(agent, scope, ProviderKey, SecretValue.FromText("x"), CancellationToken.None));
        var get = await Assert.ThrowsAsync<SecretAccessDeniedException>(() =>
            _store.GetAsync(agent, scope, ProviderKey, CallPurpose, CancellationToken.None));
        var delete = await Assert.ThrowsAsync<SecretAccessDeniedException>(() =>
            _store.DeleteAsync(agent, scope, ProviderKey, CancellationToken.None));
        var erase = await Assert.ThrowsAsync<SecretAccessDeniedException>(() =>
            _store.CryptoEraseAsync(agent, Alpha, CancellationToken.None));

        Assert.Equal("agents cannot reach the secret store", put.Reason);
        Assert.Equal("agents cannot reach the secret store", get.Reason);
        Assert.Equal("agents cannot reach the secret store", delete.Reason);
        Assert.Equal("agents cannot reach the secret store", erase.Reason);
        Assert.Equal(4, _audit.Events.Count(e => e.Outcome == SecretAuditOutcome.Denied));
    }

    [Fact]
    public async Task QuarantinedAgentScopeIsUnreadableWhileInstallationStillWorks()
    {
        await _agents.RegisterAsync(Alpha, CancellationToken.None);
        var scope = SecretScope.ForAgent(Alpha);
        await _store.PutAsync(Gateway, scope, new SecretName("token"), SecretValue.FromText("alpha-token"), CancellationToken.None);
        await _store.PutAsync(Gateway, Installation, ProviderKey, SecretValue.FromText("installation"), CancellationToken.None);
        await _agents.SetStateAsync(Alpha, AgentState.Quarantined, CancellationToken.None);

        var denied = await Assert.ThrowsAsync<SecretAccessDeniedException>(() =>
            _store.GetAsync(Gateway, scope, new SecretName("token"), CallPurpose, CancellationToken.None));
        using var installation = await _store.GetAsync(Gateway, Installation, ProviderKey, CallPurpose, CancellationToken.None);

        Assert.Equal("agent is Quarantined", denied.Reason);
        Assert.Equal("installation", Encoding.UTF8.GetString(installation.Bytes));
    }

    [Fact]
    public async Task UnregisteredAgentScopeIsDenied()
    {
        var denied = await Assert.ThrowsAsync<SecretAccessDeniedException>(() =>
            _store.PutAsync(Gateway, SecretScope.ForAgent(Beta), ProviderKey, SecretValue.FromText("x"), CancellationToken.None));

        Assert.Equal("agent is not registered", denied.Reason);
    }

    [Fact]
    public async Task AuditIsWrittenBeforeAnyWriteOrRead()
    {
        _audit.FailWrites = true;

        await Assert.ThrowsAsync<IOException>(() =>
            _store.PutAsync(Gateway, Installation, ProviderKey, SecretValue.FromText("secret"), CancellationToken.None));

        Assert.Null(await _records.GetSecretAsync("installation", ProviderKey.Value, CancellationToken.None));
        Assert.Null(await _records.GetScopeKeyAsync("installation", CancellationToken.None));

        _audit.FailWrites = false;
        await _store.PutAsync(Gateway, Installation, ProviderKey, SecretValue.FromText("secret"), CancellationToken.None);
        _audit.FailWrites = true;

        await Assert.ThrowsAsync<IOException>(() =>
            _store.GetAsync(Gateway, Installation, ProviderKey, CallPurpose, CancellationToken.None));
    }

    [Fact]
    public async Task EachAgentGetsItsOwnWrappedDataKey()
    {
        await _agents.RegisterAsync(Alpha, CancellationToken.None);
        await _agents.RegisterAsync(Beta, CancellationToken.None);
        await _store.PutAsync(Gateway, SecretScope.ForAgent(Alpha), ProviderKey, SecretValue.FromText("a"), CancellationToken.None);
        await _store.PutAsync(Gateway, SecretScope.ForAgent(Beta), ProviderKey, SecretValue.FromText("b"), CancellationToken.None);

        var alphaKey = await _records.GetScopeKeyAsync("agent:alpha", CancellationToken.None);
        var betaKey = await _records.GetScopeKeyAsync("agent:beta", CancellationToken.None);

        Assert.NotNull(alphaKey);
        Assert.NotNull(betaKey);
        Assert.False(alphaKey.WrappedKey.Span.SequenceEqual(betaKey.WrappedKey.Span));
    }

    [Fact]
    public async Task CiphertextCopiedToAnotherScopeFailsToOpen()
    {
        await _store.PutAsync(Gateway, Installation, ProviderKey, SecretValue.FromText("secret"), CancellationToken.None);
        var stored = await _records.GetSecretAsync("installation", ProviderKey.Value, CancellationToken.None);
        Assert.NotNull(stored);
        await _records.PutSecretAsync(stored with { ScopeKey = "installation", Name = "other.key" }, CancellationToken.None);

        await Assert.ThrowsAnyAsync<CryptographicException>(() =>
            _store.GetAsync(Gateway, Installation, new SecretName("other.key"), CallPurpose, CancellationToken.None));
    }

    [Fact]
    public async Task TamperedCiphertextFailsToOpen()
    {
        await _store.PutAsync(Gateway, Installation, ProviderKey, SecretValue.FromText("secret"), CancellationToken.None);
        var stored = await _records.GetSecretAsync("installation", ProviderKey.Value, CancellationToken.None);
        Assert.NotNull(stored);
        var flipped = stored.Ciphertext.ToArray();
        flipped[0] ^= 0x01;
        await _records.PutSecretAsync(stored with { Ciphertext = flipped }, CancellationToken.None);

        await Assert.ThrowsAnyAsync<CryptographicException>(() =>
            _store.GetAsync(Gateway, Installation, ProviderKey, CallPurpose, CancellationToken.None));
    }

    [Fact]
    public async Task VersionsAdvanceAndTheLatestWins()
    {
        await _store.PutAsync(Gateway, Installation, ProviderKey, SecretValue.FromText("v1"), CancellationToken.None);
        await _store.PutAsync(Gateway, Installation, ProviderKey, SecretValue.FromText("v2"), CancellationToken.None);

        using var read = await _store.GetAsync(Gateway, Installation, ProviderKey, CallPurpose, CancellationToken.None);
        var stored = await _records.GetSecretAsync("installation", ProviderKey.Value, CancellationToken.None);

        Assert.Equal("v2", Encoding.UTF8.GetString(read.Bytes));
        Assert.Equal(2, stored!.Version);
    }

    [Fact]
    public async Task CryptoEraseMakesTheAgentScopeUnreadableAndLeavesOthersAlone()
    {
        await _agents.RegisterAsync(Alpha, CancellationToken.None);
        await _agents.RegisterAsync(Beta, CancellationToken.None);
        var alphaScope = SecretScope.ForAgent(Alpha);
        await _store.PutAsync(Gateway, alphaScope, ProviderKey, SecretValue.FromText("alpha"), CancellationToken.None);
        await _store.PutAsync(Gateway, SecretScope.ForAgent(Beta), ProviderKey, SecretValue.FromText("beta"), CancellationToken.None);

        await _store.CryptoEraseAsync(Gateway, Alpha, CancellationToken.None);

        await Assert.ThrowsAsync<SecretNotFoundException>(() =>
            _store.GetAsync(Gateway, alphaScope, ProviderKey, CallPurpose, CancellationToken.None));
        var erased = await Assert.ThrowsAsync<SecretAccessDeniedException>(() =>
            _store.PutAsync(Gateway, alphaScope, ProviderKey, SecretValue.FromText("again"), CancellationToken.None));
        Assert.Equal("this scope's data key was crypto-erased", erased.Reason);
        Assert.NotNull((await _records.GetScopeKeyAsync("agent:alpha", CancellationToken.None))!.DestroyedAt);
        Assert.True((await _records.GetScopeKeyAsync("agent:alpha", CancellationToken.None))!.WrappedKey.IsEmpty);

        using var beta = await _store.GetAsync(Gateway, SecretScope.ForAgent(Beta), ProviderKey, CallPurpose, CancellationToken.None);
        Assert.Equal("beta", Encoding.UTF8.GetString(beta.Bytes));
        Assert.Contains(_audit.Events, e => e.Action == SecretAuditAction.CryptoErase && e.Outcome == SecretAuditOutcome.Allowed);
    }

    [Fact]
    public async Task DeleteRemovesTheSecretAndRejectsRepeats()
    {
        await _store.PutAsync(Gateway, Installation, ProviderKey, SecretValue.FromText("x"), CancellationToken.None);

        await _store.DeleteAsync(Gateway, Installation, ProviderKey, CancellationToken.None);

        await Assert.ThrowsAsync<SecretNotFoundException>(() =>
            _store.DeleteAsync(Gateway, Installation, ProviderKey, CancellationToken.None));
        await Assert.ThrowsAsync<SecretNotFoundException>(() =>
            _store.GetAsync(Gateway, Installation, ProviderKey, CallPurpose, CancellationToken.None));
    }

    [Fact]
    public async Task AuditEventsNeverCarryTheValue()
    {
        const string secret = "sk-very-secret-value-42";
        await _store.PutAsync(Gateway, Installation, ProviderKey, SecretValue.FromText(secret), CancellationToken.None);
        using (await _store.GetAsync(Gateway, Installation, ProviderKey, CallPurpose, CancellationToken.None)) { }

        var dump = string.Join("|", _audit.Events.Select(e => $"{e.Caller} {e.ScopeKey} {e.Name} {e.Purpose} {e.Reason}"));

        Assert.DoesNotContain(secret, dump, StringComparison.Ordinal);
        Assert.Equal(2, _audit.Events.Count);
        Assert.All(_audit.Events, e => Assert.Equal(SecretAuditOutcome.Allowed, e.Outcome));
    }
}

public sealed class SecretValueAndIdentifierTests
{
    [Fact]
    public void SecretValueRedactsAndZeroesOnDispose()
    {
        var value = SecretValue.FromText("hunter2");

        Assert.Equal("[redacted]", value.ToString());
        Assert.Equal("hunter2", Encoding.UTF8.GetString(value.Bytes));

        value.Dispose();

        Assert.Throws<ObjectDisposedException>(() => value.Bytes.Length);
    }

    [Fact]
    public void SecretValueRejectsEmptyInput()
    {
        Assert.Throws<ArgumentException>(() => new SecretValue(ReadOnlySpan<byte>.Empty));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Bad Id")]
    [InlineData("-leading")]
    [InlineData("../escape")]
    public void AgentIdRejectsUnsafeValues(string value)
    {
        Assert.ThrowsAny<ArgumentException>(() => new AgentId(value));
    }

    [Theory]
    [InlineData("")]
    [InlineData("UPPER")]
    [InlineData("../x")]
    public void SecretNameRejectsUnsafeValues(string value)
    {
        Assert.ThrowsAny<ArgumentException>(() => new SecretName(value));
    }

    [Theory]
    [InlineData("")]
    [InlineData("1starts-with-digit")]
    [InlineData("Gateway Call")]
    public void SecretPurposeRejectsUnsafeValues(string value)
    {
        Assert.ThrowsAny<ArgumentException>(() => new SecretPurpose(value));
    }

    [Fact]
    public void ScopeKeysAreStableAndDistinct()
    {
        Assert.Equal("installation", SecretScope.Installation.Key);
        Assert.Equal("agent:alpha", SecretScope.ForAgent(new AgentId("alpha")).Key);
    }
}

public sealed class SoftwareKekProviderTests
{
    [Fact]
    public async Task WrapAndUnwrapRoundTripsAndBindsAssociatedData()
    {
        using var kek = new SoftwareKekProvider("kek-1", RandomNumberGenerator.GetBytes(32));
        var key = RandomNumberGenerator.GetBytes(32);
        var aad = Encoding.UTF8.GetBytes("scope-a");

        var wrapped = await kek.WrapAsync(key, aad, CancellationToken.None);
        var unwrapped = await kek.UnwrapAsync("kek-1", wrapped, aad, CancellationToken.None);

        Assert.True(unwrapped.Span.SequenceEqual(key));
        await Assert.ThrowsAnyAsync<CryptographicException>(async () =>
            await kek.UnwrapAsync("kek-1", wrapped, Encoding.UTF8.GetBytes("scope-b"), CancellationToken.None));
    }

    [Fact]
    public async Task UnwrapRejectsAnUnknownKekIdAndTruncatedInput()
    {
        using var kek = new SoftwareKekProvider("kek-1", RandomNumberGenerator.GetBytes(32));
        var wrapped = await kek.WrapAsync(RandomNumberGenerator.GetBytes(32), ReadOnlyMemory<byte>.Empty, CancellationToken.None);

        await Assert.ThrowsAnyAsync<CryptographicException>(async () =>
            await kek.UnwrapAsync("kek-2", wrapped, ReadOnlyMemory<byte>.Empty, CancellationToken.None));
        await Assert.ThrowsAnyAsync<CryptographicException>(async () =>
            await kek.UnwrapAsync("kek-1", wrapped[..10], ReadOnlyMemory<byte>.Empty, CancellationToken.None));
    }

    [Fact]
    public void KekRequiresA256BitKey()
    {
        Assert.Throws<ArgumentException>(() => new SoftwareKekProvider("kek-1", new byte[16]));
    }
}

public sealed class RevokedAgentTests
{
    [Fact]
    public async Task RevokedAgentCanStillBeCryptoErasedButNotRead()
    {
        var time = new FakeTimeProvider();
        var records = new InMemorySecretRecordRepository();
        var agents = new InMemoryAgentRegistry(time);
        var audit = new InMemorySecretAuditSink();
        using var kek = new SoftwareKekProvider("kek-1", System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var store = new SecretStore(kek, records, agents, audit, time);
        var alpha = new AgentId("alpha");
        var scope = SecretScope.ForAgent(alpha);
        var gateway = new HostCaller("gateway");
        await agents.RegisterAsync(alpha, CancellationToken.None);
        await store.PutAsync(gateway, scope, new SecretName("token"), SecretValue.FromText("t"), CancellationToken.None);
        await agents.SetStateAsync(alpha, AgentState.Revoked, CancellationToken.None);

        var denied = await Assert.ThrowsAsync<SecretAccessDeniedException>(() =>
            store.GetAsync(gateway, scope, new SecretName("token"), new SecretPurpose("gateway.call"), CancellationToken.None));
        Assert.Equal("agent is Revoked", denied.Reason);

        await store.CryptoEraseAsync(gateway, alpha, CancellationToken.None);

        Assert.NotNull((await records.GetScopeKeyAsync("agent:alpha", CancellationToken.None))!.DestroyedAt);
    }
}
