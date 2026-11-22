using Bazaar.Server.Gateway.Adapters;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Providers.ClaudeCode.Tests;

public sealed class ComponentTests
{
    private static ServiceProvider Build(Dictionary<string, string?> settings)
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        new ClaudeCodeComponent().AddServices(services, cfg);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Off_by_default_registers_nothing()
    {
        using var sp = Build([]);
        Assert.Empty(sp.GetServices<ModelAdapterRegistration>());
    }

    [Fact]
    public void Guided_setup_is_available_while_the_provider_is_off()
    {
        using var sp = Build([]);
        Assert.NotNull(sp.GetRequiredService<ClaudeCodeGuidedSetup>());
    }

    [Fact]
    public void Enabled_registers_the_adapter_under_its_wire_name()
    {
        using var sp = Build(new()
        {
            ["Bazaar:Gateway:ClaudeCode:Enabled"] = "true",
            ["Bazaar:Gateway:ClaudeCode:ConfigDir"] = "/tmp/x",
        });
        var reg = Assert.Single(sp.GetServices<ModelAdapterRegistration>());
        Assert.Equal("claude_code_cli", reg.WireName);
        Assert.NotNull(sp.GetRequiredService<IClaudeCodeLaneState>());
    }

    [Fact]
    public void Login_mode_needs_a_config_dir_one_owner_and_the_terms_acknowledgement()
    {
        var v = new ClaudeCodeOptionsValidator();
        var result = v.Validate(null, new ClaudeCodeOptions { Enabled = true });
        Assert.True(result.Failed);
        var text = string.Join('\n', result.Failures!);
        Assert.Contains("ConfigDir", text, StringComparison.Ordinal);
        Assert.Contains("LoginOwner", text, StringComparison.Ordinal);
        Assert.Contains("TermsAcknowledgedAt", text, StringComparison.Ordinal);
        Assert.True(v.Validate(null, new ClaudeCodeOptions { Enabled = true, ConfigDir = "/x", LoginOwner = "user:owner", TermsAcknowledgedAt = DateTimeOffset.UnixEpoch }).Succeeded);
    }

    [Fact]
    public void Api_key_mode_needs_a_reference_and_models_are_validated()
    {
        var v = new ClaudeCodeOptionsValidator();
        Assert.True(v.Validate(null, new ClaudeCodeOptions { Enabled = true, AuthMode = ClaudeCodeAuthMode.ApiKey }).Failed);
        var bad = v.Validate(null, new ClaudeCodeOptions { Enabled = true, AuthMode = ClaudeCodeAuthMode.ApiKey, ApiKey = "env:K", Models = ["--evil"] });
        Assert.True(bad.Failed);
        Assert.True(v.Validate(null, new ClaudeCodeOptions { Enabled = false }).Succeeded);
    }
}
