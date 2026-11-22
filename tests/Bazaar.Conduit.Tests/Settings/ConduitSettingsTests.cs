using System.Text.Json;
using Bazaar.Conduit.Settings;
using Bazaar.Server.Settings;

namespace Bazaar.Conduit.Tests.Settings;

public sealed class ConduitSettingsTests
{
    private static readonly SettingActor _owner = new("owner", SettingRole.Owner);

    private static SettingEvaluation Propose(SettingDefinition key, string json) =>
        key.Evaluate(_owner, JsonElement.Parse(json));

    [Fact]
    public void All_keys_register_without_duplicates_and_are_lower_case_conduit_keys()
    {
        var registry = SettingRegistry.Combine([], [new ConduitSettingContribution()]);

        Assert.Equal(ConduitSettings.All.Count, registry.Definitions.Count);
        Assert.All(ConduitSettings.All, key =>
        {
            Assert.StartsWith("conduit.", key.Name, StringComparison.Ordinal);
            Assert.Equal(key.Name.ToLowerInvariant(), key.Name);
            Assert.Equal(SettingScope.Install, key.Scope);
        });
    }

    [Fact]
    public void Everything_is_off_by_default_except_lan()
    {
        Assert.True(ConduitSettings.LanEnabled.Default);
        Assert.False(ConduitSettings.CloudflaredEnabled.Default);
        Assert.False(ConduitSettings.PortForwardEnabled.Default);
        Assert.False(ConduitSettings.AllowUnpinnedTlsTermination.Default);
        Assert.Equal(CloudflaredMode.Named, ConduitSettings.CloudflaredModeKey.Default);
    }

    [Fact]
    public void Turning_on_an_exposing_module_is_held_for_owner_approval_and_turning_it_off_is_not()
    {
        Assert.Equal(SettingWriteOutcome.PendingApproval, Propose(ConduitSettings.CloudflaredEnabled, "true").Outcome);
        Assert.Equal(SettingWriteOutcome.Accepted, Propose(ConduitSettings.CloudflaredEnabled, "false").Outcome);
        Assert.Equal(SettingWriteOutcome.Accepted, Propose(ConduitSettings.LanEnabled, "false").Outcome);
    }

    [Fact]
    public void Quick_mode_needs_approval_and_named_does_not()
    {
        Assert.Equal(SettingWriteOutcome.PendingApproval, Propose(ConduitSettings.CloudflaredModeKey, "\"quick\"").Outcome);
        Assert.Equal(SettingWriteOutcome.Accepted, Propose(ConduitSettings.CloudflaredModeKey, "\"named\"").Outcome);
    }

    [Theory]
    [InlineData("\"example.com\"", SettingWriteOutcome.PendingApproval)]
    [InlineData("\"\"", SettingWriteOutcome.Accepted)]
    [InlineData("\"not a host!\"", SettingWriteOutcome.Rejected)]
    public void Hostname_is_checked(string json, SettingWriteOutcome expected) =>
        Assert.Equal(expected, Propose(ConduitSettings.CloudflaredHostname, json).Outcome);

    [Theory]
    [InlineData("\"ws://relay.example\"")]
    [InlineData("\"https://relay.example\"")]
    public void Relay_url_must_be_wss(string json) =>
        Assert.Equal(SettingWriteOutcome.Rejected, Propose(ConduitSettings.RelayUrl, json).Outcome);

    [Theory]
    [InlineData("100", SettingWriteOutcome.Rejected)]
    [InlineData("4000", SettingWriteOutcome.Accepted)]
    [InlineData("120000", SettingWriteOutcome.Rejected)]
    public void Connect_timeout_has_floors(string json, SettingWriteOutcome expected) =>
        Assert.Equal(expected, Propose(ConduitSettings.ConnectTimeoutMs, json).Outcome);

    [Fact]
    public void A_non_owner_cannot_change_a_key() =>
        Assert.Equal(
            SettingWriteOutcome.Rejected,
            ConduitSettings.LanEnabled.Evaluate(new SettingActor("agent", SettingRole.Agent), JsonElement.Parse("false")).Outcome);
}
