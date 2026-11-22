using Bazaar.Server.Gateway.FreeLlmApi;
using Bazaar.Server.Secrets;
using Bazaar.Server.Tests.Gateway.Pipeline;
using Bazaar.Server.Tests.Gateway.Policy;
using Bazaar.Testing;

namespace Bazaar.Server.Tests.Gateway.FreeLlmApi;

/// <summary>
/// The FreeLLMAPI options, their start-time validation, and the rule that a disabled config adds no provider and reads no
/// secret. Every key here is a placeholder reference, never a credential.
/// </summary>
public sealed class FreeLlmApiConfigurationTests
{
    private const string _keyReference = "env:FREELLMAPI_TEST_KEY";

    private static FreeLlmApiOptions Enabled() => new()
    {
        Enabled = true,
        BaseUrl = FreeLlmApiTestRig.BaseUrl,
        ApiKey = _keyReference,
        DefaultModel = "llama-3.3-70b",
    };

    private static List<string> FailuresOf(FreeLlmApiOptions options)
    {
        var result = new FreeLlmApiOptionsValidator().Validate(null, options);
        Assert.True(result.Failed);
        return result.Failures.ToList();
    }

    [Fact]
    public void Defaults_are_off_with_auto_on_the_agent_default_route_and_a_120_second_timeout()
    {
        var options = new FreeLlmApiOptions();

        Assert.False(options.Enabled);
        Assert.Equal("auto", options.DefaultModel);
        Assert.Equal("agent-default", options.RouteName);
        Assert.Equal(120, options.TimeoutSeconds);
        Assert.True(new FreeLlmApiOptionsValidator().Validate(null, options).Succeeded);
    }

    [Fact]
    public void A_disabled_config_passes_validation_even_when_empty()
    {
        Assert.True(new FreeLlmApiOptionsValidator().Validate(null, new FreeLlmApiOptions { Enabled = false }).Succeeded);
    }

    [Fact]
    public void An_enabled_config_with_a_concrete_model_and_an_env_reference_passes()
    {
        Assert.True(new FreeLlmApiOptionsValidator().Validate(null, Enabled()).Succeeded);
    }

    [Fact]
    public void An_enabled_config_without_a_base_url_fails_and_names_the_key()
    {
        var options = Enabled();
        options.BaseUrl = "";

        var failures = FailuresOf(options);

        Assert.Contains(failures, f => f.Contains("Bazaar:Gateway:FreeLlmApi:BaseUrl", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("ftp://127.0.0.1/v1")]
    [InlineData("127.0.0.1:3001/v1")]
    [InlineData("file:///tmp/x")]
    public void The_base_url_must_be_an_absolute_http_or_https_url(string baseUrl)
    {
        var options = Enabled();
        options.BaseUrl = baseUrl;

        Assert.Contains(FailuresOf(options), f => f.Contains("BaseUrl must be an absolute http or https URL", StringComparison.Ordinal));
    }

    [Fact]
    public void A_base_url_that_carries_credentials_is_refused()
    {
        var options = Enabled();
        options.BaseUrl = "http://user:pass@127.0.0.1:3001/v1";

        var failures = FailuresOf(options);

        Assert.Contains(failures, f => f.Contains("must not carry credentials", StringComparison.Ordinal));
        Assert.DoesNotContain(failures, f => f.Contains("user:pass", StringComparison.Ordinal));
    }

    [Fact]
    public void A_literal_key_is_refused_and_never_echoed()
    {
        const string literal = "freellmapi-LITERAL-PLANTED-KEY";
        var options = Enabled();
        options.ApiKey = literal;

        var failures = FailuresOf(options);

        Assert.Contains(failures, f => f.Contains("must be a secret reference", StringComparison.Ordinal));
        Assert.DoesNotContain(failures, f => f.Contains(literal, StringComparison.Ordinal));
    }

    [Fact]
    public void The_default_model_auto_is_refused_at_start_with_a_message_that_says_why()
    {
        var options = Enabled();
        options.DefaultModel = "auto";

        Assert.Contains(FailuresOf(options), f => f.Contains("DefaultModel 'auto' is not a usable model id", StringComparison.Ordinal));
    }

    [Fact]
    public void A_route_name_that_is_not_a_valid_route_is_refused()
    {
        var options = Enabled();
        options.RouteName = "auto/fast";

        Assert.Contains(FailuresOf(options), f => f.Contains("RouteName 'auto/fast' is not a valid route name", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_disabled_config_registers_no_provider_and_reads_no_secret()
    {
        var world = new InMemoryPolicyWorld();
        var resolver = new CountingResolver();
        var fallback = new FakeSecrets();
        var disabled = new FreeLlmApiOptions { Enabled = false };

        var directory = FreeLlmApiWiring.CreateDirectory(world, disabled, new FakeClock());
        var secrets = FreeLlmApiWiring.CreateProviderSecretSource(disabled, resolver, fallback);
        await FreeLlmApiStartupCheck.VerifyKeyAsync(disabled, resolver, TestContext.Current.CancellationToken);

        Assert.Same(world, directory);
        Assert.Same(fallback, secrets);
        Assert.Null(await directory.GetProviderAsync(FreeLlmApiProvider.ProviderId, TestContext.Current.CancellationToken));
        Assert.Null(await directory.GetRouteAsync("agent-default", TestContext.Current.CancellationToken));
        Assert.Equal(0, resolver.Reads);
    }

    [Fact]
    public async Task A_missing_secret_fails_the_start_with_a_clear_error_that_names_the_reference()
    {
        var resolver = new BootstrapSecretResolver(_ => null);
        var options = Enabled();
        options.ApiKey = "env:FREELLMAPI_TEST_MISSING";

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            FreeLlmApiStartupCheck.VerifyKeyAsync(options, resolver, TestContext.Current.CancellationToken));

        Assert.Contains("Bazaar:Gateway:FreeLlmApi:ApiKey", error.Message, StringComparison.Ordinal);
        Assert.Contains("env:FREELLMAPI_TEST_MISSING", error.Message, StringComparison.Ordinal);
        Assert.Contains("is not set", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_present_secret_lets_the_start_continue()
    {
        var resolver = new BootstrapSecretResolver(name => name == "FREELLMAPI_TEST_KEY" ? "present-value" : null);

        await FreeLlmApiStartupCheck.VerifyKeyAsync(Enabled(), resolver, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task The_start_error_never_contains_a_value_that_was_read()
    {
        const string planted = "PLANTED-EMPTY-FILE-SENTINEL";
        var resolver = new BootstrapSecretResolver(name => name == "FREELLMAPI_TEST_KEY" ? "   " : planted);
        var options = Enabled();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            FreeLlmApiStartupCheck.VerifyKeyAsync(options, resolver, TestContext.Current.CancellationToken));

        Assert.DoesNotContain(planted, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(planted, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_secret_source_serves_the_key_for_the_freellmapi_reference_only()
    {
        var resolver = new CountingResolver();
        var fallback = new FakeSecrets();
        fallback.Values["secret://bazaar/providers/other/api-key"] = "other-provider-value";
        var secrets = FreeLlmApiWiring.CreateProviderSecretSource(Enabled(), resolver, fallback);

        var other = await secrets.ResolveAsync("secret://bazaar/providers/other/api-key", TestContext.Current.CancellationToken);
        var own = await secrets.ResolveAsync(FreeLlmApiProvider.SecretRef, TestContext.Current.CancellationToken);

        Assert.Equal("other-provider-value", other);
        Assert.Equal(CountingResolver.Value, own);
        Assert.Equal(1, resolver.Reads);
    }

    /// <summary>Counts reads and returns a fixed value, so a test can see whether a secret was read at all.</summary>
    private sealed class CountingResolver : ISecretResolver
    {
        public const string Value = "counted-value";

        public int Reads { get; private set; }

        public bool CanResolve(string reference) => true;

        public ValueTask<string> ResolveAsync(string reference, CancellationToken cancellationToken)
        {
            Reads++;
            return ValueTask.FromResult(Value);
        }
    }
}
