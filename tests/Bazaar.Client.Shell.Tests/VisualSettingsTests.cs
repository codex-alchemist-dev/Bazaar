using Bazaar.Client.Materials;
using Bazaar.Client.Motion;
using Bazaar.Client.Motion.Avalonia;
using Bazaar.Client.Settings;
using Bazaar.Client.Theme;

namespace Bazaar.Client.Shell.Tests;

public class VisualSettingsTests
{
    private sealed class Rig
    {
        public Rig(MotionPlatform platform = MotionPlatform.Desktop, bool os = false)
        {
            Store = new ClientSettingsStore(VisualSettings.Definitions);
            Materials = new PolledMaterialEnvironmentSource(() => new MaterialEnvironment(MaterialPlatform.Windows, TransparencyAvailable: true));
            Motion = new MotionSettings(platform);
            OsFlag = os;
            Os = new PolledOsReducedMotionSource(() => OsFlag);
            Monitor = new FrameBudgetMonitor(new FrameBudgetOptions(TimeSpan.FromMilliseconds(16.67), WindowSize: 20, MinSamples: 10, RecoverEvaluations: 1));
            Binder = new VisualSettingsBinder(Store, Materials, Motion, Os, Monitor);
        }

        public bool OsFlag { get; set; }

        public ClientSettingsStore Store { get; }

        public PolledMaterialEnvironmentSource Materials { get; }

        public MotionSettings Motion { get; }

        public PolledOsReducedMotionSource Os { get; }

        public FrameBudgetMonitor Monitor { get; }

        public VisualSettingsBinder Binder { get; }
    }

    [Fact]
    public void The_shipped_defaults_are_auto_materials_platform_motion_comfortable_and_void()
    {
        var rig = new Rig();

        Assert.Equal(MaterialLevel.Auto, rig.Materials.Level);
        Assert.Null(rig.Motion.Setting);
        Assert.Equal(MotionLevel.Subtle, rig.Motion.Effective);
        Assert.Equal(ClientDensity.Comfortable, rig.Binder.Density);
        Assert.Equal(BundledThemes.Default, VisualSettings.LoadTheme(rig.Store).Name, ignoreCase: true);
    }

    [Fact]
    public void The_materials_setting_reaches_the_policy_source_live()
    {
        var rig = new Rig();

        rig.Store.TrySetValue(MaterialSettings.Key, "opaque");
        Assert.Equal(MaterialLevel.Opaque, rig.Materials.Level);
        Assert.Equal(MaterialReason.SettingOpaque, new MaterialPolicy(rig.Materials).Resolve(MaterialSurface.Chrome).Reason);

        rig.Store.TrySetValue(MaterialSettings.Key, "translucent");
        Assert.Equal(MaterialLevel.Translucent, rig.Materials.Level);
    }

    [Fact]
    public void The_motion_setting_reaches_the_motion_host_state_and_clearing_restores_the_platform_default()
    {
        var rig = new Rig(MotionPlatform.Phone);
        Assert.Equal(MotionLevel.Liquid, rig.Motion.Effective);

        rig.Store.TrySetValue(MotionSettingKeys.MotionLevel, "reduced");
        Assert.Equal(MotionLevel.Reduced, rig.Motion.Effective);

        rig.Store.TrySetValue(MotionSettingKeys.MotionLevel, null);
        Assert.Equal(MotionLevel.Liquid, rig.Motion.Effective);
    }

    [Fact]
    public void The_os_flag_forces_reduced_and_follows_changes()
    {
        var rig = new Rig(os: true);
        rig.Store.TrySetValue(MotionSettingKeys.MotionLevel, "liquid");
        Assert.Equal(MotionLevel.Reduced, rig.Motion.Effective);

        rig.OsFlag = false;
        rig.Os.Refresh();

        Assert.Equal(MotionLevel.Liquid, rig.Motion.Effective);
    }

    [Fact]
    public void Density_changes_raise_an_event()
    {
        var rig = new Rig();
        int raised = 0;
        rig.Binder.DensityChanged += (_, _) => raised++;

        rig.Store.TrySetValue(ClientDensitySettings.Key, "compact");
        rig.Store.TrySetValue(ClientDensitySettings.Key, "compact");

        Assert.Equal(ClientDensity.Compact, rig.Binder.Density);
        Assert.Equal(1, raised);
    }

    [Theory]
    [InlineData("graphite", "graphite")]
    [InlineData("paper", "paper")]
    [InlineData("imported", "void")]
    public void The_theme_setting_picks_a_bundled_theme_and_imported_falls_back_to_the_default(string value, string expected)
    {
        var rig = new Rig();
        rig.Store.TrySetValue(ClientThemeSettings.Key, value);

        Assert.Equal(expected, VisualSettings.LoadTheme(rig.Store).Name, ignoreCase: true);
    }

    [Fact]
    public void A_frame_budget_miss_flags_the_materials_and_caps_motion_then_lifts_with_hysteresis()
    {
        var rig = new Rig(MotionPlatform.Phone);
        rig.Store.TrySetValue(MotionSettingKeys.MotionLevel, "liquid");

        for (int i = 0; i < 10; i++)
        {
            rig.Monitor.Record(TimeSpan.FromMilliseconds(30));
        }

        Assert.True(rig.Materials.FrameBudgetMissed);
        Assert.Equal(MotionLevel.Subtle, rig.Motion.Effective);

        for (int i = 0; i < 10; i++)
        {
            rig.Monitor.Record(TimeSpan.FromMilliseconds(70));
        }

        Assert.Equal(MotionLevel.Reduced, rig.Motion.Effective);

        // Good frames lift Heavy to Light, then Light to None (RecoverEvaluations is 1 here).
        for (int i = 0; i < 40; i++)
        {
            rig.Monitor.Record(TimeSpan.FromMilliseconds(16));
        }

        Assert.False(rig.Materials.FrameBudgetMissed);
        Assert.Equal(MotionLevel.Liquid, rig.Motion.Effective);
    }

    [Fact]
    public void Changes_are_marshalled_through_post()
    {
        var queue = new Queue<Action>();
        var store = new ClientSettingsStore(VisualSettings.Definitions);
        var materials = new PolledMaterialEnvironmentSource(() => new MaterialEnvironment(MaterialPlatform.Windows));
        using var binder = new VisualSettingsBinder(store, materials, new MotionSettings(MotionPlatform.Desktop), post: queue.Enqueue);

        store.TrySetValue(MaterialSettings.Key, "opaque");
        Assert.Equal(MaterialLevel.Auto, materials.Level);

        while (queue.Count > 0)
        {
            queue.Dequeue()();
        }

        Assert.Equal(MaterialLevel.Opaque, materials.Level);
    }

    [Fact]
    public void A_disposed_binder_stops_following_the_settings()
    {
        var rig = new Rig();
        rig.Binder.Dispose();

        rig.Store.TrySetValue(MaterialSettings.Key, "opaque");

        Assert.Equal(MaterialLevel.Auto, rig.Materials.Level);
    }
}
