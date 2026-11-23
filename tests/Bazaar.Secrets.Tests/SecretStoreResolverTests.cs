using System.Security.Cryptography;
using Bazaar.Secrets.Fakes;
using Bazaar.Server.Secrets;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Secrets.Tests;

public sealed class SecretStoreResolverTests : IDisposable
{
    private const string _planted = "sk-planted-value-9f3";
    private static readonly HostCaller _admin = new("admin");
    private static readonly SecretPurpose _readPurpose = new("secret-reference.resolve");

    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-11-18T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
    private readonly SoftwareKekProvider _kek = new("kek-test", RandomNumberGenerator.GetBytes(32));
    private readonly SecretStore _store;
    private readonly SecretStoreResolver _resolver;

    public SecretStoreResolverTests() : this(grantResolver: true)
    {
    }

    private SecretStoreResolverTests(bool grantResolver)
    {
        List<SecretGrant> grants = [new("admin", SecretScope.Installation.Key, null, SecretPurposes.Manage)];
        if (grantResolver)
        {
            grants.Add(SecretStoreResolver.RequiredGrant);
        }

        _store = new SecretStore(_kek, new InMemorySecretRecordRepository(), new InMemoryAgentRegistry(_time), new StaticSecretPolicy(grants), new InMemorySecretAuditSink(), _time);
        _resolver = new SecretStoreResolver(_store);
    }

    public void Dispose() => _kek.Dispose();

    private Task PutAsync(string name, string value) =>
        _store.PutAsync(_admin, SecretScope.Installation, new SecretName(name), SecretValue.FromText(value), CancellationToken.None);

    [Fact]
    public async Task ResolvesAStoredSecretAndLowercasesThePath()
    {
        await PutAsync("providers.anthropic.key", _planted);

        Assert.Equal(_planted, await _resolver.ResolveAsync("secret://bazaar/providers/anthropic/key", CancellationToken.None));
        Assert.Equal(_planted, await _resolver.ResolveAsync("secret://bazaar/Providers/Anthropic/Key", CancellationToken.None));
    }

    [Fact]
    public void HandlesOnlyTheBazaarSecretPrefix()
    {
        Assert.True(_resolver.CanResolve("secret://bazaar/a/b"));
        Assert.False(_resolver.CanResolve("env:HOME"));
        Assert.False(_resolver.CanResolve("secret://other/a"));
    }

    [Fact]
    public async Task WrongPrefixIsRefused()
    {
        await Assert.ThrowsAsync<SecretResolutionException>(async () => await _resolver.ResolveAsync("env:HOME", CancellationToken.None));
    }

    [Fact]
    public async Task UnknownReferenceFailsClosed()
    {
        var ex = await Assert.ThrowsAsync<SecretResolutionException>(async () =>
            await _resolver.ResolveAsync("secret://bazaar/providers/missing/key", CancellationToken.None));
        Assert.Equal("secret://bazaar/providers/missing/key", ex.Reference);
    }

    [Theory]
    [InlineData("secret://bazaar/")]
    [InlineData("secret://bazaar/a//b")]
    [InlineData("secret://bazaar/a/_b")]
    [InlineData("secret://bazaar/a b")]
    public async Task MalformedPathIsAResolutionError(string reference)
    {
        await Assert.ThrowsAsync<SecretResolutionException>(async () => await _resolver.ResolveAsync(reference, CancellationToken.None));
    }

    [Fact]
    public async Task DeniedReadFailsClosedWithoutTheValueInTheMessage()
    {
        using var denied = new SecretStoreResolverTests(grantResolver: false);
        await denied.PutAsync("providers.anthropic.key", _planted);

        var ex = await Assert.ThrowsAsync<SecretResolutionException>(async () =>
            await denied._resolver.ResolveAsync("secret://bazaar/providers/anthropic/key", CancellationToken.None));
        Assert.DoesNotContain(_planted, ex.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StoreFaultIsMappedToAFixedReasonWithoutInnerDetail()
    {
        var resolver = new SecretStoreResolver(new ThrowingStore(new InvalidOperationException(_planted)));

        var ex = await Assert.ThrowsAsync<SecretResolutionException>(async () =>
            await resolver.ResolveAsync("secret://bazaar/a/b", CancellationToken.None));
        Assert.DoesNotContain(_planted, ex.ToString(), StringComparison.Ordinal);
        Assert.Null(ex.InnerException);
    }

    private sealed class ThrowingStore(Exception error) : ISecretStore
    {
        public Task PutAsync(SecretCaller caller, SecretScope scope, SecretName name, SecretValue value, CancellationToken ct) => throw error;

        public Task<SecretValue> GetAsync(SecretCaller caller, SecretScope scope, SecretName name, SecretPurpose purpose, CancellationToken ct) => throw error;

        public Task DeleteAsync(SecretCaller caller, SecretScope scope, SecretName name, CancellationToken ct) => throw error;

        public Task CryptoEraseAsync(SecretCaller caller, AgentId agent, CancellationToken ct) => throw error;
    }
}
