using Bazaar.Server.Gateway.Secrets;
using Bazaar.Server.Secrets;

namespace Bazaar.Server.Tests.Gateway.Secrets;

public class GatewaySecretSourceTests
{
    private static readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private const string _providerRef = "secret://bazaar/providers/alpha/key";

    [Theory]
    [InlineData("env:LEAKCHECK")]
    [InlineData("file:/tmp/LEAKCHECK")]
    [InlineData("dpapi:LEAKCHECK")]
    [InlineData("secret://other/providers/LEAKCHECK")]
    [InlineData("secret://bazaar/providers/")]
    [InlineData("LEAKCHECK")]
    [InlineData("")]
    public async Task Refs_outside_the_provider_prefix_are_rejected_without_being_echoed(string secretRef)
    {
        var resolver = new FakeResolver(_ => "never");
        var source = new GatewaySecretSource(resolver);

        var ex = await Assert.ThrowsAsync<ArgumentException>(async () => await source.ResolveAsync(secretRef, _ct));

        Assert.DoesNotContain("LEAKCHECK", ex.Message, StringComparison.Ordinal);
        Assert.Empty(resolver.Calls);
    }

    [Fact]
    public async Task Null_ref_is_rejected_by_argument_check()
    {
        var source = new GatewaySecretSource(new FakeResolver(_ => "never"));

        await Assert.ThrowsAsync<ArgumentNullException>(async () => await source.ResolveAsync(null!, _ct));
    }

    [Fact]
    public async Task Provider_ref_delegates_to_the_resolver_and_returns_the_value()
    {
        var resolver = new FakeResolver(_ => "sk-live-123");
        var source = new GatewaySecretSource(resolver);

        var value = await source.ResolveAsync(_providerRef, _ct);

        Assert.Equal("sk-live-123", value);
        Assert.Equal([_providerRef], resolver.Calls);
    }

    [Fact]
    public async Task Missing_secret_returns_null_when_the_resolver_reports_not_found()
    {
        var resolver = new FakeResolver(r => throw new SecretResolutionException(r, "the file does not exist."));
        var source = new GatewaySecretSource(resolver);

        Assert.Null(await source.ResolveAsync(_providerRef, _ct));
    }

    [Fact]
    public async Task Empty_secret_value_returns_null()
    {
        var source = new GatewaySecretSource(new FakeResolver(_ => "   "));

        Assert.Null(await source.ResolveAsync(_providerRef, _ct));
    }

    [Fact]
    public async Task Ref_no_resolver_understands_is_missing_and_never_read()
    {
        var resolver = new FakeResolver(_ => "never") { CanResolveResult = false };
        var source = new GatewaySecretSource(resolver);

        Assert.Null(await source.ResolveAsync(_providerRef, _ct));
        Assert.Empty(resolver.Calls);
    }

    [Fact]
    public async Task No_environment_fallback_when_the_real_bootstrap_resolver_is_in_place()
    {
        // The environment would hold a value under the reference's own name. The source must not look there.
        var reads = new List<string>();
        var bootstrap = new BootstrapSecretResolver(name =>
        {
            reads.Add(name);
            return "env-value-must-not-surface";
        });
        var source = new GatewaySecretSource(bootstrap);

        Assert.Null(await source.ResolveAsync(_providerRef, _ct));
        Assert.Empty(reads);
    }

    private sealed class FakeResolver(Func<string, string> behaviour) : ISecretResolver
    {
        public List<string> Calls { get; } = [];

        public bool CanResolveResult { get; set; } = true;

        public bool CanResolve(string reference) => CanResolveResult;

        public ValueTask<string> ResolveAsync(string reference, CancellationToken cancellationToken)
        {
            Calls.Add(reference);
            return ValueTask.FromResult(behaviour(reference));
        }
    }
}
