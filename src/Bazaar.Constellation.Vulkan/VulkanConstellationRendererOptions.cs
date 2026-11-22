using Bazaar.Constellation.Rendering.Core;

namespace Bazaar.Constellation.Vulkan;

/// <summary>
/// Tunables for <see cref="VulkanConstellationRenderer"/>. Every default is provisional; the host should set
/// <see cref="DisplayRefreshHz"/> from the display the window is on.
/// </summary>
public sealed record VulkanConstellationRendererOptions
{
    /// <summary>Display maximum refresh rate in Hz, the full-rate target for big movement. Default 60 until the host reports the real rate.</summary>
    public double DisplayRefreshHz { get; init; } = 60d;

    /// <summary>Pacing thresholds and holds. Null uses the <see cref="FramePacerOptions"/> defaults.</summary>
    public FramePacerOptions? Pacer { get; init; }

    /// <summary>Time source for pacing. Null uses a monotonic stopwatch; tests inject a manual clock.</summary>
    public Func<TimeSpan>? Clock { get; init; }
}
