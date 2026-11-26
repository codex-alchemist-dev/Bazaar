using System.Text.Json;
using Bazaar.Server.Gc;
using Bazaar.Server.Gc.Members;
using Bazaar.Server.Settings;

namespace Bazaar.Server.Tests.Gc;

/// <summary>The <c>gc.*</c> setting keys: defaults, validation and registration (database-free).</summary>
public sealed class GcSettingsTests
{
    [Fact]
    public void Invite_policy_defaults_to_any_member()
    {
        Assert.Equal(GcInvitePolicy.AnyMember, GcSettings.InvitePolicyKey.Default);
    }

    [Fact]
    public void Edit_window_defaults_to_unlimited()
    {
        Assert.Equal(0, GcSettings.EditWindowHoursKey.Default);
    }

    [Fact]
    public void A_negative_edit_window_is_refused()
    {
        using var doc = JsonDocument.Parse("-1");

        Assert.NotNull(GcSettings.EditWindowHoursKey.CheckValue(doc.RootElement));
    }

    [Fact]
    public void Both_gc_keys_are_registered_in_the_known_settings()
    {
        Assert.True(BuiltInSettingRegistries.Known.TryGet("gc.invite_policy", out var invite));
        Assert.True(BuiltInSettingRegistries.Known.TryGet("gc.edit_window_hours", out var window));
        Assert.Same(GcSettings.InvitePolicyKey, invite);
        Assert.Same(GcSettings.EditWindowHoursKey, window);
    }

    [Fact]
    public void The_doc_01_defaults_hold_for_the_policy_keys()
    {
        Assert.Equal(GcUnarchivePolicy.Owner, GcSettings.UnarchivePolicyKey.Default);
        Assert.Equal(GcDmAccessApprovers.Owner, GcSettings.DmAccessApproversKey.Default);
        Assert.Equal(GcBroadcastMentions.OwnerAndAgentsRateLimited, GcSettings.BroadcastMentionsKey.Default);
        Assert.Equal(2, GcSettings.BroadcastMentionRatePerHourKey.Default);
        Assert.Equal(GcVaultEmbedPin.Commit, GcSettings.VaultEmbedPinKey.Default);
        Assert.Equal(32768, GcSettings.MessageMaxBytesKey.Default);
        Assert.Equal(26214400, GcSettings.BlobMaxBytesKey.Default);
    }

    [Fact]
    public void Changing_dm_access_approvers_needs_owner_approval()
    {
        Assert.Equal(ApprovalTier.Tier2, GcSettings.DmAccessApproversKey.ApprovalTier);
    }

    [Fact]
    public void Non_positive_size_caps_are_refused()
    {
        using var zero = JsonDocument.Parse("0");

        Assert.NotNull(GcSettings.MessageMaxBytesKey.CheckValue(zero.RootElement));
        Assert.NotNull(GcSettings.BlobMaxBytesKey.CheckValue(zero.RootElement));
    }

    [Fact]
    public void Every_gc_key_is_registered_in_the_known_settings()
    {
        foreach (var definition in GcSettings.All)
        {
            Assert.True(BuiltInSettingRegistries.Known.TryGet(definition.Name, out var registered), definition.Name);
            Assert.Same(definition, registered);
        }
    }
}
