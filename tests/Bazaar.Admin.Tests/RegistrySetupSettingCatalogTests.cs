using System.Text.Json;
using Bazaar.Admin.Composition;
using Bazaar.Conduit.Settings;
using Bazaar.Server.BackupPolicy;
using Bazaar.Server.Gateway;
using Bazaar.Server.MailPolicy;
using Bazaar.Server.Modules.Settings;
using Bazaar.Server.Settings;
using Bazaar.Server.Setup;
using Bazaar.Server.Setup.Host;

namespace Bazaar.Admin.Tests;

/// <summary>The seam between the server's settings registry and the setup library.</summary>
public sealed class RegistrySetupSettingCatalogTests
{
    private static JsonElement Json(string text) => JsonElement.Parse(text);

    [Fact]
    public void Every_registered_key_is_exposed_by_name()
    {
        var catalog = RegistrySetupSettingCatalog.Known;
        var registry = BuiltInSettingRegistries.WithContributions([new ModuleManagerSettingContribution()]);

        var registry = BuiltInSettingRegistries.WithContributions(
            [new ModuleManagerSettingContribution(), new BackupPolicySettingContribution(), new MailPolicySettingContribution(), new ConduitSettingContribution(), new GatewaySettingContribution()]);

        // The built-in keys plus the module contributions the admin tool composes (module manager, backup policy, mail policy, Conduit).
        Assert.Equal(registry.Definitions.Count, catalog.Definitions.Count);
        Assert.True(catalog.TryGet("update.channel", out _));
        Assert.All(registry.Definitions, d => Assert.True(catalog.TryGet(d.Name, out var found) && found!.Name == d.Name));
        Assert.False(catalog.TryGet("no.such.key", out _));
    }

    [Theory]
    [InlineData("backup.target")]
    [InlineData("backup.drill_interval_days")]
    [InlineData("backup.no_target_acknowledged")]
    [InlineData("mail.enabled")]
    [InlineData("mail.backend")]
    [InlineData("update.channel")]
    [InlineData("deps.update.autoSecurityPatches")]
    public void The_keys_a_plan_projects_from_modules_are_known_install_settings(string key)
    {
        Assert.True(RegistrySetupSettingCatalog.Known.TryGet(key, out var definition));
        Assert.True(definition!.IsInstallScope);
    }

    [Fact]
    public void Scope_and_type_follow_the_registry_definition()
    {
        Assert.True(RegistrySetupSettingCatalog.Known.TryGet("host.status.port", out var port));

        Assert.True(port!.IsInstallScope);
        Assert.Equal(typeof(int), port.ValueType);
        Assert.NotNull(port.CheckValue(Json("\"text\"")));
        Assert.Null(port.CheckValue(Json("8443")));
    }

    [Fact]
    public void A_non_loopback_status_bind_is_held_for_approval_and_loopback_is_accepted()
    {
        Assert.True(RegistrySetupSettingCatalog.Known.TryGet("host.status.bind", out var bind));

        var held = bind!.Evaluate("plan", Json("\"all\""));
        var accepted = bind.Evaluate("plan", Json("\"loopback\""));

        Assert.False(held.Accepted);
        Assert.NotNull(held.Reason);
        Assert.True(accepted.Accepted);
    }

    [Fact]
    public async Task The_audit_adapter_writes_one_accepted_install_row()
    {
        var sink = new InMemorySettingAuditSink();

        await new SettingAuditSinkSetupAudit(sink).AppendInstallChangeAsync("host.status.port", "operator", "0", "8443", TestContext.Current.CancellationToken);

        var row = Assert.Single(sink.Entries);
        Assert.Equal("host.status.port", row.Key);
        Assert.Equal(SettingScope.Install, row.Scope);
        Assert.Equal(string.Empty, row.ScopeId);
        Assert.Equal("operator", row.ActorId);
        Assert.Equal(SettingWriteOutcome.Accepted, row.Outcome);
        Assert.Equal("0", row.OldValueJson);
        Assert.Equal("8443", row.NewValueJson);
    }
}
