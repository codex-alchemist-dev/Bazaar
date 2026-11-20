using System.Text.Json;
using Bazaar.Server.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.MailPolicy.Tests;

/// <summary>The mail policy keys (doc 22, doc 45 step 11): registration, safe defaults and the back end names.</summary>
public sealed class MailPolicySettingsTests
{
    private static readonly SettingActor _operator = new("setup:plan", SettingRole.Operator);

    private static readonly SettingRegistry _known = SettingRegistry.WithContributions([new MailPolicySettingContribution()]);

    private static SettingEvaluation Eval(string name, string json)
    {
        Assert.True(_known.TryGet(name, out var definition));
        Assert.NotNull(definition);
        using var doc = JsonDocument.Parse(json);
        return definition.Evaluate(_operator, doc.RootElement);
    }

    [Fact]
    public void The_contribution_registers_exactly_the_two_keys()
    {
        var names = new MailPolicySettingContribution().Settings.Select(d => d.Name).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(["mail.backend", "mail.enabled"], names);
        foreach (var definition in MailPolicySettings.All)
        {
            Assert.True(_known.Contains(definition), definition.Name);
            Assert.Equal(SettingScope.Install, definition.Scope);
            Assert.Equal(SettingRole.Operator, definition.WhoMayChange);
            Assert.Equal(ApprovalTier.None, definition.ApprovalTier);
        }
    }

    [Fact]
    public void Mail_is_off_with_no_back_end_by_default()
    {
        Assert.False(MailPolicySettings.Enabled.Default);
        Assert.Equal(MailBackendKind.None, MailPolicySettings.Backend.Default);
        Assert.Equal("\"none\"", MailPolicySettings.Backend.DefaultValueJson);
    }

    [Theory]
    [InlineData("bazaar_client")]
    [InlineData("imap_smtp")]
    [InlineData("cloudflare")]
    [InlineData("builtin_server")]
    [InlineData("none")]
    public void Every_documented_back_end_id_is_accepted_in_wire_form(string id)
    {
        var evaluation = Eval("mail.backend", $"\"{id}\"");

        Assert.Equal(SettingWriteOutcome.Accepted, evaluation.Outcome);
        Assert.Equal($"\"{id}\"", evaluation.ValueJson);
    }

    [Theory]
    [InlineData("\"gmail\"")]
    [InlineData("\"\"")]
    [InlineData("3")]
    public void An_unknown_back_end_is_rejected(string json)
    {
        Assert.Equal(SettingWriteOutcome.Rejected, Eval("mail.backend", json).Outcome);
    }

    [Fact]
    public void The_plan_ids_match_the_enum_wire_forms()
    {
        var wire = Enum.GetValues<MailBackendKind>().Where(kind => kind != MailBackendKind.None)
            .Select(kind => Eval("mail.backend", JsonSerializer.Serialize(Snake(kind))).ValueJson.Trim('"'))
            .ToArray();

        Assert.Equal(MailPolicySettingNames.BackendIds.Order(StringComparer.Ordinal), wire.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void The_enabled_flag_wants_a_bool()
    {
        Assert.Equal(SettingWriteOutcome.Accepted, Eval("mail.enabled", "true").Outcome);
        Assert.Equal(SettingWriteOutcome.Rejected, Eval("mail.enabled", "\"on\"").Outcome);
    }

    [Fact]
    public void The_component_registers_the_contribution_and_declares_no_dependency()
    {
        var services = new ServiceCollection();

        new MailPolicyComponent().AddServices(services, new ConfigurationBuilder().Build());

        using var provider = services.BuildServiceProvider();
        Assert.Single(provider.GetServices<ISettingContribution>());
        Assert.Empty(MailPolicyComponent.ManifestDefinition.DependsOn);
    }

    private static string Snake(MailBackendKind kind) => kind switch
    {
        MailBackendKind.BazaarClient => "bazaar_client",
        MailBackendKind.ImapSmtp => "imap_smtp",
        MailBackendKind.Cloudflare => "cloudflare",
        MailBackendKind.BuiltinServer => "builtin_server",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
