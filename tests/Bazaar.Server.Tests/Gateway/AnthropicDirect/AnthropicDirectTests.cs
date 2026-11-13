using Bazaar.Contracts.Gateway;
using Bazaar.Server.Components;
using Bazaar.Server.Gateway.AnthropicDirect;
using Bazaar.Server.Gateway.Data;
using Bazaar.Server.Gateway.Pipeline;
using Bazaar.Server.Gateway.Policy;
using Bazaar.Server.Secrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Tests.Gateway.AnthropicDirect;

/// <summary>
/// The owner's Anthropic key as a config-defined gateway source: off by default, a directory overlay with native identity, a key
/// read only through a secret reference, and a refusal of subscription tokens. Every key here is a placeholder, never a credential.
/// </summary>
public sealed class AnthropicDirectTests
{
    private const string _keyReference = "env:ANTHROPIC_TEST_KEY";

    // Placeholders with the real prefixes, built from parts so no scanner reads them as credentials.
    private static readonly string _apiKey = "sk-ant-" + "api03-placeholder";
    private static readonly string _oauthAccessToken = "sk-ant-" + "oat01-placeholder";
    private static readonly string _oauthRefreshToken = "sk-ant-" + "ort01-placeholder";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    private static (string Key, string Value)[] EnabledValues() =>
    [
        ("Bazaar:Gateway:AnthropicDirect:Enabled", "true"),
        ("Bazaar:Gateway:AnthropicDirect:ApiKey", _keyReference),
    ];

    private static AnthropicDirectOptions Enabled(params string[] models) =>
        new() { Enabled = true, ApiKey = _keyReference, Models = [.. models] };

    private static ValidateOptionsResult Validate(AnthropicDirectOptions options) =>
        new AnthropicDirectOptionsValidator().Validate(null, options);

    // ---- options ----

    [Fact]
    public void The_source_is_off_by_default_and_a_disabled_config_is_valid_whatever_else_it_holds()
    {
        var options = new AnthropicDirectOptions();

        Assert.False(options.Enabled);
        Assert.Equal("https://api.anthropic.com", options.BaseUrl);
        Assert.True(Validate(options).Succeeded);
        Assert.True(Validate(new AnthropicDirectOptions { BaseUrl = "not a url", ApiKey = "literal" }).Succeeded);
    }

    [Fact]
    public void With_no_models_configured_the_source_serves_haiku_only()
    {
        Assert.Equal(["claude-haiku-5-5"], Enabled().EffectiveModels());
        Assert.Equal(["claude-haiku-5-5"], Enabled(" ", "").EffectiveModels());
    }

    [Fact]
    public void Configured_models_are_trimmed_and_listed_once()
    {
        Assert.Equal(["claude-haiku-5-5", "claude-sonnet-5-5"], Enabled(" claude-haiku-5-5 ", "claude-sonnet-5-5", "claude-haiku-5-5").EffectiveModels());
    }

    [Fact]
    public void An_enabled_config_with_a_key_reference_is_valid()
    {
        Assert.True(Validate(Enabled()).Succeeded);
        Assert.True(Validate(new AnthropicDirectOptions { Enabled = true, ApiKey = "file:/run/secrets/anthropic" }).Succeeded);
    }

    [Fact]
    public void Enabled_without_a_key_reference_fails_and_names_the_setting()
    {
        var result = Validate(new AnthropicDirectOptions { Enabled = true });

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("Bazaar:Gateway:AnthropicDirect:ApiKey is required", StringComparison.Ordinal));
    }

    [Fact]
    public void A_literal_key_is_refused_and_not_echoed()
    {
        var result = Validate(new AnthropicDirectOptions { Enabled = true, ApiKey = _apiKey });

        Assert.True(result.Failed);
        var failure = Assert.Single(result.Failures!);
        Assert.Contains("must be a secret reference", failure, StringComparison.Ordinal);
        Assert.DoesNotContain(_apiKey, failure, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("api.anthropic.com")]
    [InlineData("ftp://api.anthropic.com")]
    [InlineData("https://user:pass@api.anthropic.com")]
    public void A_bad_base_url_fails(string baseUrl)
    {
        var result = Validate(new AnthropicDirectOptions { Enabled = true, ApiKey = _keyReference, BaseUrl = baseUrl });

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("Bazaar:Gateway:AnthropicDirect:BaseUrl", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("auto/claude")]
    [InlineData("Claude Haiku")]
    [InlineData("claude:haiku")]
    public void A_model_that_cannot_be_a_model_id_and_a_route_name_fails(string model)
    {
        var result = Validate(Enabled(model));

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("Bazaar:Gateway:AnthropicDirect:Models", StringComparison.Ordinal));
    }

    // ---- directory ----

    private static AnthropicDirectGatewayDirectory Directory(AnthropicDirectOptions options, IGatewayDirectory? inner = null) =>
        new(inner ?? new EmptyDirectory(), options, TimeProvider.System);

    [Fact]
    public async Task The_provider_is_first_party_anthropic_messages_with_an_api_key_reference()
    {
        var provider = await Directory(Enabled()).GetProviderAsync("anthropic-direct", Ct);

        Assert.NotNull(provider);
        Assert.Equal("first_party", provider.Kind);
        Assert.Equal("anthropic_messages", provider.Adapter);
        Assert.Equal("https://api.anthropic.com", provider.BaseUrl);
        Assert.Equal("api_key", provider.AuthKind);
        Assert.Equal(AnthropicDirectProvider.SecretRef, provider.SecretRef);
        Assert.True(provider.Enabled);
        Assert.True(CredentialKinds.IsAcceptable(provider.AuthKind, provider.SecretRef));
    }

    [Fact]
    public async Task A_trailing_slash_on_the_base_url_is_removed()
    {
        var options = Enabled();
        options.BaseUrl = "https://proxy.example/anthropic/";

        Assert.Equal("https://proxy.example/anthropic", (await Directory(options).GetProviderAsync("anthropic-direct", Ct))!.BaseUrl);
    }

    [Fact]
    public async Task Each_model_is_native_claude_and_has_a_route_named_after_it()
    {
        var directory = Directory(Enabled("claude-haiku-5-5", "claude-sonnet-5-5"));

        foreach (var id in new[] { "claude-haiku-5-5", "claude-sonnet-5-5" })
        {
            var member = new ModelRef("anthropic-direct", id);
            var model = await directory.GetModelAsync(member, Ct);
            Assert.NotNull(model);
            Assert.Equal(GatewayIdentity.Native, model.Identity);
            Assert.Equal("claude", model.Family);
            Assert.Equal(GwModel.StatusAvailable, model.Status);

            var route = await directory.GetRouteAsync(id, Ct);
            Assert.NotNull(route);
            Assert.Equal(RouteStrategy.Priority, route.Strategy);
            Assert.Equal([member], route.Members);
            Assert.Empty(route.ExpectedSet);
        }
    }

    [Fact]
    public async Task A_model_that_is_not_configured_has_no_model_and_no_route()
    {
        var directory = Directory(Enabled());

        Assert.Null(await directory.GetModelAsync(new ModelRef("anthropic-direct", "claude-opus-5-5"), Ct));
        Assert.Null(await directory.GetRouteAsync("claude-opus-5-5", Ct));
    }

    [Fact]
    public async Task Anything_else_goes_to_the_inner_directory()
    {
        var inner = new EmptyDirectory();
        var directory = Directory(Enabled(), inner);

        await directory.GetProviderAsync("openai", Ct);
        await directory.GetModelAsync(new ModelRef("openai", "gpt"), Ct);
        await directory.GetRouteAsync("agent-default", Ct);

        Assert.Equal(["provider:openai", "model:openai:gpt", "route:agent-default"], inner.Asked);
    }

    [Fact]
    public async Task The_configured_route_wins_over_a_stored_route_of_the_same_name()
    {
        var inner = new EmptyDirectory();
        var route = await Directory(Enabled(), inner).GetRouteAsync("claude-haiku-5-5", Ct);

        Assert.Equal([new ModelRef("anthropic-direct", "claude-haiku-5-5")], route!.Members);
        Assert.Empty(inner.Asked);
    }

    // ---- wiring ----

    [Fact]
    public void A_disabled_source_leaves_the_directory_and_the_secret_source_unchanged()
    {
        var inner = new EmptyDirectory();
        var fallback = new FixedSecretSource("other");
        var resolver = new FixedResolver(_apiKey);

        Assert.Same(inner, AnthropicDirectWiring.CreateDirectory(inner, new AnthropicDirectOptions(), TimeProvider.System));
        Assert.Same(fallback, AnthropicDirectWiring.CreateProviderSecretSource(new AnthropicDirectOptions(), resolver, fallback));
        Assert.Empty(resolver.Asked);
    }

    [Fact]
    public void An_enabled_source_wraps_both()
    {
        Assert.IsType<AnthropicDirectGatewayDirectory>(
            AnthropicDirectWiring.CreateDirectory(new EmptyDirectory(), Enabled(), TimeProvider.System));
        Assert.IsType<AnthropicDirectSecretSource>(
            AnthropicDirectWiring.CreateProviderSecretSource(Enabled(), new FixedResolver(_apiKey), new FixedSecretSource("other")));
    }

    // ---- secrets ----

    [Fact]
    public async Task The_key_is_read_through_the_configured_reference_for_the_provider_reference_only()
    {
        var resolver = new FixedResolver(_apiKey);
        var source = new AnthropicDirectSecretSource(_keyReference, resolver, new FixedSecretSource("other"));

        Assert.Equal(_apiKey, await source.ResolveAsync(AnthropicDirectProvider.SecretRef, Ct));
        Assert.Equal([_keyReference], resolver.Asked);

        Assert.Equal("other", await source.ResolveAsync("secret://bazaar/providers/openai/api-key", Ct));
        Assert.Equal([_keyReference], resolver.Asked);
    }

    [Fact]
    public async Task The_key_is_read_again_on_every_call_so_a_rotation_applies_without_a_restart()
    {
        var resolver = new FixedResolver(_apiKey);
        var source = new AnthropicDirectSecretSource(_keyReference, resolver, new FixedSecretSource("other"));

        await source.ResolveAsync(AnthropicDirectProvider.SecretRef, Ct);
        resolver.Value = _apiKey + "-rotated";

        Assert.Equal(_apiKey + "-rotated", await source.ResolveAsync(AnthropicDirectProvider.SecretRef, Ct));
    }

    [Fact]
    public async Task A_subscription_token_is_refused_and_the_message_does_not_show_it()
    {
        foreach (var token in new[] { _oauthAccessToken, _oauthRefreshToken, "  " + _oauthAccessToken.ToUpperInvariant() })
        {
            var source = new AnthropicDirectSecretSource(_keyReference, new FixedResolver(token), new FixedSecretSource("other"));

            var ex = await Assert.ThrowsAsync<AnthropicCredentialRefusedException>(
                async () => await source.ResolveAsync(AnthropicDirectProvider.SecretRef, Ct));

            Assert.Contains("subscription", ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(token.Trim(), ex.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task An_empty_key_is_refused()
    {
        var source = new AnthropicDirectSecretSource(_keyReference, new FixedResolver("  "), new FixedSecretSource("other"));

        await Assert.ThrowsAsync<AnthropicCredentialRefusedException>(
            async () => await source.ResolveAsync(AnthropicDirectProvider.SecretRef, Ct));
    }

    [Fact]
    public void An_api_key_is_not_a_subscription_token()
    {
        Assert.False(AnthropicDirectCredential.IsSubscriptionToken(_apiKey));
        Assert.True(AnthropicDirectCredential.IsSubscriptionToken(_oauthAccessToken));
        Assert.True(AnthropicDirectCredential.IsSubscriptionToken(_oauthRefreshToken));
    }

    // ---- startup check ----

    [Fact]
    public async Task The_startup_check_reads_no_secret_when_the_source_is_off()
    {
        var resolver = new FixedResolver(_apiKey);

        await AnthropicDirectStartupCheck.VerifyKeyAsync(new AnthropicDirectOptions { ApiKey = _keyReference }, resolver, Ct);

        Assert.Empty(resolver.Asked);
    }

    [Fact]
    public async Task The_startup_check_passes_with_an_api_key()
    {
        await AnthropicDirectStartupCheck.VerifyKeyAsync(Enabled(), new FixedResolver(_apiKey), Ct);
    }

    [Fact]
    public async Task The_startup_check_fails_and_names_the_setting_when_the_key_cannot_be_resolved()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => AnthropicDirectStartupCheck.VerifyKeyAsync(Enabled(), new FixedResolver(null), Ct));

        Assert.Contains("Bazaar:Gateway:AnthropicDirect:ApiKey could not be resolved", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_startup_check_fails_for_a_subscription_token()
    {
        await Assert.ThrowsAsync<AnthropicCredentialRefusedException>(
            () => AnthropicDirectStartupCheck.VerifyKeyAsync(Enabled(), new FixedResolver(_oauthAccessToken), Ct));
    }

    // ---- component ----

    private static ServiceCollection Register(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddBazaarComponents(configuration, [new AnthropicDirectComponent()]);
        return services;
    }

    [Fact]
    public void Manifest_is_a_provider_with_no_dependencies()
    {
        var manifest = new AnthropicDirectComponent().Manifest;

        Assert.Equal("anthropic-direct", manifest.Id);
        Assert.Equal(ComponentKind.Provider, manifest.Kind);
        Assert.Empty(manifest.DependsOn);
        Assert.Equal(["gateway:provider:anthropic-direct"], manifest.Provides);
    }

    [Fact]
    public void A_disabled_source_registers_no_options_and_no_startup_check()
    {
        var services = Register(Config());

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IHostedService));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IValidateOptions<AnthropicDirectOptions>));
        Assert.False(AnthropicDirectComponent.IsEnabled(Config()));
    }

    [Fact]
    public void An_enabled_source_binds_its_options_and_registers_the_startup_check()
    {
        var values = EnabledValues().Append(("Bazaar:Gateway:AnthropicDirect:Models:0", "claude-sonnet-5-5")).ToArray();
        var services = Register(Config(values));
        services.AddSingleton<ISecretResolver>(new FixedResolver(_apiKey));

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AnthropicDirectOptions>>().Value;

        Assert.True(options.Enabled);
        Assert.Equal(_keyReference, options.ApiKey);
        Assert.Equal(["claude-sonnet-5-5"], options.EffectiveModels());
        Assert.IsType<AnthropicDirectStartupCheckService>(Assert.Single(provider.GetServices<IHostedService>()));
    }

    [Fact]
    public void An_enabled_source_with_a_literal_key_fails_options_validation()
    {
        var services = Register(Config(("Bazaar:Gateway:AnthropicDirect:Enabled", "true"), ("Bazaar:Gateway:AnthropicDirect:ApiKey", _apiKey)));

        using var provider = services.BuildServiceProvider();
        var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<AnthropicDirectOptions>>().Value);

        Assert.DoesNotContain(_apiKey, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Gateway_on_with_the_component_disabled_fails_startup_and_names_both_settings()
    {
        var configuration = Config([.. EnabledValues(), ("Bazaar:Components:anthropic-direct:Enabled", "false")]);

        var ex = Assert.Throws<InvalidOperationException>(() => AnthropicDirectComponent.RequireComponentWhenGatewayIsOn(configuration));

        Assert.Contains("Bazaar:Gateway:AnthropicDirect:Enabled", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Bazaar:Components:anthropic-direct:Enabled=false", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Gateway_off_or_component_on_passes_the_startup_rule()
    {
        AnthropicDirectComponent.RequireComponentWhenGatewayIsOn(Config());
        AnthropicDirectComponent.RequireComponentWhenGatewayIsOn(Config(EnabledValues()));
        AnthropicDirectComponent.RequireComponentWhenGatewayIsOn(Config(("Bazaar:Components:anthropic-direct:Enabled", "false")));
    }

    // ---- fakes ----

    private sealed class EmptyDirectory : IGatewayDirectory
    {
        public List<string> Asked { get; } = [];

        public ValueTask<GwProvider?> GetProviderAsync(string providerId, CancellationToken cancellationToken)
        {
            Asked.Add($"provider:{providerId}");
            return ValueTask.FromResult<GwProvider?>(null);
        }

        public ValueTask<GwModel?> GetModelAsync(ModelRef model, CancellationToken cancellationToken)
        {
            Asked.Add($"model:{model}");
            return ValueTask.FromResult<GwModel?>(null);
        }

        public ValueTask<GwRoute?> GetRouteAsync(string routeId, CancellationToken cancellationToken)
        {
            Asked.Add($"route:{routeId}");
            return ValueTask.FromResult<GwRoute?>(null);
        }
    }

    private sealed class FixedSecretSource(string value) : IProviderSecretSource
    {
        public ValueTask<string?> ResolveAsync(string secretRef, CancellationToken ct) => ValueTask.FromResult<string?>(value);
    }

    /// <summary>Returns one value for any reference, or fails every resolution when the value is null.</summary>
    private sealed class FixedResolver(string? value) : ISecretResolver
    {
        public string? Value { get; set; } = value;

        public List<string> Asked { get; } = [];

        public bool CanResolve(string reference) => true;

        public ValueTask<string> ResolveAsync(string reference, CancellationToken cancellationToken)
        {
            Asked.Add(reference);
            if (Value is null)
            {
                throw new SecretResolutionException(reference, "not found");
            }

            return ValueTask.FromResult(Value);
        }
    }
}
