using Bazaar.Constellation.Rendering.Core;

namespace Bazaar.Constellation.Vulkan.Tests;

/// <summary>On-demand pacing of the Vulkan renderer. A manual clock drives it, so no GPU or window is needed.</summary>
public class VulkanRendererPacingTests
{
    private static readonly VulkanProbeResult _probe = new(true, string.Empty, "1.4.0", []);

    private sealed class ManualClock
    {
        public TimeSpan Now { get; set; }
    }

    private static VulkanConstellationRenderer Create(ManualClock clock, double hz = 120) =>
        new(() => _probe, options: new VulkanConstellationRendererOptions { DisplayRefreshHz = hz, Clock = () => clock.Now });

    [Fact]
    public void First_frame_is_due_at_construction_and_then_idle_skips()
    {
        var clock = new ManualClock();
        using VulkanConstellationRenderer renderer = Create(clock);
        Assert.Equal(TimeSpan.Zero, renderer.NextFrameDelay);
        Assert.True(renderer.TryBeginFrame());
        Assert.Equal(1, renderer.FramesRendered);

        clock.Now = TimeSpan.FromSeconds(5);
        Assert.Equal(PacerMode.Idle, renderer.PacerMode);
        Assert.Null(renderer.NextFrameDelay);
        Assert.False(renderer.TryBeginFrame());
        Assert.Equal(1, renderer.FramesRendered);
    }

    [Fact]
    public void Input_wakes_a_standstill_renderer_at_once()
    {
        var clock = new ManualClock();
        using VulkanConstellationRenderer renderer = Create(clock);
        renderer.TryBeginFrame();
        clock.Now = TimeSpan.FromSeconds(5);
        Assert.False(renderer.TryBeginFrame());

        renderer.OnCameraInput(0f); // a button press without movement still wakes
        Assert.Equal(TimeSpan.Zero, renderer.NextFrameDelay);
        Assert.True(renderer.TryBeginFrame());
    }

    [Fact]
    public void Small_input_runs_below_the_display_rate_and_big_input_at_it()
    {
        var clock = new ManualClock();
        using VulkanConstellationRenderer renderer = Create(clock, 120);
        renderer.TryBeginFrame();
        clock.Now = TimeSpan.FromSeconds(5);

        renderer.OnCameraInput(10f);
        Assert.Equal(PacerMode.InteractingSmall, renderer.PacerMode);
        Assert.True(renderer.TryBeginFrame());
        clock.Now += TimeSpan.FromMilliseconds(5);
        // Small movement targets 90 Hz (about 11.1 ms): 5 ms after a frame the next one must wait.
        Assert.True(renderer.NextFrameDelay > TimeSpan.Zero);

        renderer.OnCameraInput(400f);
        Assert.Equal(PacerMode.InteractingBig, renderer.PacerMode);
        clock.Now += TimeSpan.FromMilliseconds(4); // 9 ms: past the 8.3 ms interval at 120 Hz
        Assert.Equal(TimeSpan.Zero, renderer.NextFrameDelay);
    }

    [Fact]
    public void Scene_style_and_resize_changes_wake_the_pacer()
    {
        var clock = new ManualClock();
        using VulkanConstellationRenderer renderer = Create(clock);
        renderer.TryBeginFrame();
        clock.Now = TimeSpan.FromSeconds(5);
        Assert.Null(renderer.NextFrameDelay);

        renderer.Resize(640, 480);
        Assert.Equal(TimeSpan.Zero, renderer.NextFrameDelay);
        renderer.TryBeginFrame();
        clock.Now = TimeSpan.FromSeconds(10);
        Assert.Null(renderer.NextFrameDelay);

        renderer.SetSections([]);
        Assert.Equal(TimeSpan.Zero, renderer.NextFrameDelay);
        renderer.TryBeginFrame();
        clock.Now = TimeSpan.FromSeconds(15);
        Assert.Null(renderer.NextFrameDelay);

        renderer.RequestFrame();
        Assert.Equal(TimeSpan.Zero, renderer.NextFrameDelay);
    }

    [Fact]
    public void Zoom_input_counts_as_movement_and_refresh_rate_can_change()
    {
        var clock = new ManualClock();
        using VulkanConstellationRenderer renderer = Create(clock, 60);
        renderer.TryBeginFrame();
        clock.Now = TimeSpan.FromSeconds(5);
        renderer.OnZoomInput(4f); // ln 4 * 1000 px is well over the big threshold
        Assert.Equal(PacerMode.InteractingBig, renderer.PacerMode);

        renderer.SetDisplayRefreshHz(144);
        Assert.Throws<ArgumentOutOfRangeException>(() => renderer.SetDisplayRefreshHz(0));
    }

    [Fact]
    public void Options_default_to_a_provisional_60_hz()
    {
        Assert.Equal(60d, new VulkanConstellationRendererOptions().DisplayRefreshHz);
    }
}
