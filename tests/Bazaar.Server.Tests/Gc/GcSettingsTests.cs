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
        Assert.True(SettingRegistry.Known.TryGet("gc.invite_policy", out var invite));
        Assert.True(SettingRegistry.Known.TryGet("gc.edit_window_hours", out var window));
        Assert.Same(GcSettings.InvitePolicyKey, invite);
        Assert.Same(GcSettings.EditWindowHoursKey, window);
    }
}
