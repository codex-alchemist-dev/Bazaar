using Bazaar.Client.Mobile;

namespace Bazaar.Client.Mobile.Tests;

public class AndroidSettingsTests
{
    [Fact]
    public void Defaults_match_the_android_design_table()
    {
        var settings = new AndroidSettings();

        Assert.Equal(["Reply", "Approvals", "Kanban", "Agents", "Annotate", "Sketch", "Start screen"], settings.RadialSectors);
        Assert.Equal(HalfOpenLayoutPolicy.UnfoldedIfWide, settings.HalfOpenLayout);
        Assert.False(settings.FoldedFullDiff);
        Assert.True(settings.MergeFromFolded);
        Assert.Equal(PushProviderKind.UnifiedPush, settings.PushProvider);
        Assert.False(settings.VoiceKeepAudio);
        Assert.Empty(settings.Validate());
    }

    [Fact]
    public void Fresh_biometric_approval_for_merges_is_always_required()
    {
        Assert.True(AndroidSettings.RequiresFreshBiometricForMerge);
    }

    [Fact]
    public void Validate_reports_empty_repeated_and_blank_sectors()
    {
        Assert.NotEmpty(new AndroidSettings { RadialSectors = [] }.Validate());
        Assert.NotEmpty(new AndroidSettings { RadialSectors = ["Reply", "Reply"] }.Validate());
        Assert.NotEmpty(new AndroidSettings { RadialSectors = ["Reply", " "] }.Validate());
    }

    [Fact]
    public void The_gesture_edge_defaults_to_24_dp_and_never_drops_below_its_floor()
    {
        Assert.Equal(24, new AndroidSettings().Gestures.EdgeInsetDp);
        Assert.Equal(GestureOptions.EdgeInsetFloorDp, new AndroidSettings { GestureEdgeDp = 4 }.Gestures.EdgeInsetDp);
    }
}
