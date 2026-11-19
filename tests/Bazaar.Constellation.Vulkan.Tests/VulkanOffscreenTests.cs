using System.Diagnostics;
using Bazaar.Constellation.Rendering.Core;

namespace Bazaar.Constellation.Vulkan.Tests;

/// <summary>
/// Renders known node sets to an offscreen image on the physical GPU, reads the pixels back, and checks them.
/// These need no window or headless extension. They skip where the loader finds no usable device.
/// </summary>
public class VulkanOffscreenTests
{
    /// <summary>Environment variable that names a file to receive GPU and CPU frame times. Unset means the timing test is skipped.</summary>
    private const string _gpuTimingOutputVariable = "BAZAAR_VULKAN_GPU_TIMING_OUT";

    /// <summary>Environment variable that names a file to receive the validation report. Unset means no file is written.</summary>
    private const string _validationOutputVariable = "BAZAAR_VULKAN_VALIDATION_OUT";

    private const uint _size = 256;
    private const int _tolerance = 2;

    // Clear colour in the offscreen image: (0.05, 0.06, 0.08) as bytes B, G, R.
    private static readonly byte[] _backgroundBgra = [20, 15, 13, 255];

    private static VulkanOffscreenRenderer CreateOrSkip(uint size, bool validation = false)
    {
        try
        {
            return VulkanOffscreenRenderer.Create(size, size, validation);
        }
        catch (InvalidOperationException ex)
        {
            Assert.Skip(ex.Message);
            throw;
        }
    }

    /// <summary>Uploads the palette of <paramref name="styles"/> to the renderer and packs the instances for <paramref name="positions"/>.</summary>
    private static NodeInstanceBuffer Prepare(VulkanOffscreenRenderer renderer, float[] positions, NodeStyleTable styles)
    {
        NodeStylePalette palette = NodeStylePalette.Build(styles);
        renderer.SetStyles(NodeStylePacking.Pack(palette));
        var instances = new NodeInstanceBuffer();
        instances.Fill(positions, 2, palette);
        return instances;
    }

    /// <summary>Byte offset of pixel (x, y) in B, G, R, A order, with row 0 at the top.</summary>
    private static int Offset(int x, int y) => (y * (int)_size + x) * 4;

    /// <summary>NDC point to pixel: NDC y runs top (-1) to bottom (+1), like the framebuffer rows.</summary>
    private static (int X, int Y) ToPixel(float x, float y) =>
        ((int)MathF.Floor((x + 1f) * 0.5f * _size), (int)MathF.Floor((y + 1f) * 0.5f * _size));

    private static bool NearBackground(ReadOnlySpan<byte> pixels, int offset) =>
        Math.Abs(pixels[offset] - _backgroundBgra[0]) <= _tolerance
        && Math.Abs(pixels[offset + 1] - _backgroundBgra[1]) <= _tolerance
        && Math.Abs(pixels[offset + 2] - _backgroundBgra[2]) <= _tolerance;

    private static bool Near(ReadOnlySpan<byte> pixels, int offset, byte b, byte g, byte r) =>
        Math.Abs(pixels[offset] - b) <= _tolerance
        && Math.Abs(pixels[offset + 1] - g) <= _tolerance
        && Math.Abs(pixels[offset + 2] - r) <= _tolerance;

    /// <summary>The pixel at an NDC point, as a byte offset into the readback.</summary>
    private static int OffsetAt(float x, float y)
    {
        (int px, int py) = ToPixel(x, y);
        return Offset(px, py);
    }

    [Fact]
    public void Readback_pixels_inside_each_node_disc_are_not_background()
    {
        using VulkanOffscreenRenderer renderer = CreateOrSkip(_size);

        // Three known nodes in NDC, with disjoint discs: red, green and blue.
        float[] centres = [-0.5f, -0.5f, 0.5f, 0.4f, 0f, 0f];
        var styles = new NodeStyleTable(3, NodeStyle.Create(0f, 0f, 0f, 1f, 0.1f));
        styles.Set(0, NodeStyle.Create(1f, 0f, 0f, 1f, 0.2f));
        styles.Set(1, NodeStyle.Create(0f, 1f, 0f, 1f, 0.15f));
        styles.Set(2, NodeStyle.Create(0f, 0f, 1f, 1f, 0.3f));
        NodeInstanceBuffer instances = Prepare(renderer, centres, styles);

        renderer.Render((0.05f, 0.06f, 0.08f, 1f), instances.Bytes, instances.InstanceCount, NodeView.Identity);
        ReadOnlySpan<byte> pixels = renderer.ReadPixels();

        // Expected colour for each node: B, G, R. Red node is index 0, green is 1, blue is 2.
        (byte B, byte G, byte R)[] expected = [(0, 0, 255), (0, 255, 0), (255, 0, 0)];
        float[] radii = [0.2f, 0.15f, 0.3f];
        for (int node = 0; node < 3; node++)
        {
            float cx = centres[node * 2];
            float cy = centres[node * 2 + 1];
            (int X, int Y) centre = ToPixel(cx, cy);
            int centreOffset = Offset(centre.X, centre.Y);
            Assert.True(
                Near(pixels, centreOffset, expected[node].B, expected[node].G, expected[node].R),
                $"node {node} centre pixel ({centre.X},{centre.Y}) is B{pixels[centreOffset]} G{pixels[centreOffset + 1]} R{pixels[centreOffset + 2]}, expected B{expected[node].B} G{expected[node].G} R{expected[node].R}");

            // Eight points on a ring at 70% of the radius lie inside the disc and must not be background.
            for (int step = 0; step < 8; step++)
            {
                double angle = step * Math.PI / 4;
                float px = cx + (float)(Math.Cos(angle) * radii[node] * 0.7);
                float py = cy + (float)(Math.Sin(angle) * radii[node] * 0.7);
                (int X, int Y) pixel = ToPixel(px, py);
                Assert.False(
                    NearBackground(pixels, Offset(pixel.X, pixel.Y)),
                    $"node {node} ring point {step} at pixel ({pixel.X},{pixel.Y}) is background");
            }
        }

        // A point outside every disc (more than 1.4 from each centre) must be background.
        (int X, int Y) outside = ToPixel(-0.9f, 0.9f);
        Assert.True(NearBackground(pixels, Offset(outside.X, outside.Y)), "a point outside the discs is not background");
    }

    [Fact]
    public void Two_nodes_with_different_styles_render_in_their_own_colours_and_radii()
    {
        using VulkanOffscreenRenderer renderer = CreateOrSkip(_size);

        // Node 0: a filled red disc, radius 0.2, on the left. Node 1: a green ring, radius 0.3, ring width 0.5, on the right.
        // The discs do not overlap: node 0 spans x in [-0.65, -0.25], node 1 spans x in [0.15, 0.75].
        float[] centres = [-0.45f, 0f, 0.45f, 0f];
        var styles = new NodeStyleTable(2, NodeStyle.Create(0f, 0f, 0f, 1f, 0f));
        styles.Set(0, NodeStyle.Create(1f, 0f, 0f, 1f, 0.2f));
        styles.Set(1, NodeStyle.Create(0f, 1f, 0f, 1f, 0.3f, ringWidth: 0.5f));
        NodeInstanceBuffer instances = Prepare(renderer, centres, styles);
        Assert.Equal(2, NodeStylePalette.Build(styles).StyleCount);

        renderer.Render((0.05f, 0.06f, 0.08f, 1f), instances.Bytes, instances.InstanceCount, NodeView.Identity);
        ReadOnlySpan<byte> pixels = renderer.ReadPixels();

        // Node 0 is a filled red disc: its centre and a point at 90% of its radius are red, and a point at 120% is background.
        int redCentre = OffsetAt(-0.45f, 0f);
        Assert.True(Near(pixels, redCentre, 0, 0, 255), "the filled node's centre is not red");
        Assert.True(Near(pixels, OffsetAt(-0.45f + 0.9f * 0.2f, 0f), 0, 0, 255), "the filled node is not drawn at 90% of its radius");
        Assert.True(NearBackground(pixels, OffsetAt(-0.45f + 1.2f * 0.2f, 0f)), "the filled node is drawn beyond its radius");

        // Node 1 is a ring: its centre is the cut-out hole (background); a point at 75% of the radius is in the band
        // (green); a point at 40% of the radius is inside the hole; a point at 120% of the radius is outside the ring.
        Assert.True(NearBackground(pixels, OffsetAt(0.45f, 0f)), "the ring's centre is not cut out");
        Assert.True(Near(pixels, OffsetAt(0.45f + 0.75f * 0.3f, 0f), 0, 255, 0), "the ring band is not green at 75% of its radius");
        Assert.True(NearBackground(pixels, OffsetAt(0.45f + 0.4f * 0.3f, 0f)), "the ring's hole is filled");
        Assert.True(NearBackground(pixels, OffsetAt(0.45f + 1.2f * 0.3f, 0f)), "the ring is drawn beyond its radius");

        // The two nodes use their own radii: node 0 at 0.25 (beyond its 0.2 radius) is background, node 1 at 0.25 in
        // the band is green. This separates the radius of each style from the other's.
        Assert.True(NearBackground(pixels, OffsetAt(-0.45f + 0.25f, 0f)), "the filled node's radius leaked past 0.2");
    }

    [Fact]
    public void Validation_layer_reports_no_errors_for_a_node_frame()
    {
        using VulkanOffscreenRenderer renderer = CreateOrSkip(64, validation: true);
        VulkanOffscreenRenderer.DrainValidationMessages();

        var styles = new NodeStyleTable(2, NodeStyle.Create(0.8f, 0.86f, 0.95f, 1f, 0.2f));
        NodeInstanceBuffer instances = Prepare(renderer, new float[] { -0.3f, 0f, 0.3f, 0f }, styles);
        renderer.Render((0.05f, 0.06f, 0.08f, 1f), instances.Bytes, instances.InstanceCount, NodeView.Identity);

        IReadOnlyList<VulkanValidationMessage> messages = VulkanOffscreenRenderer.DrainValidationMessages();
        string? reportPath = Environment.GetEnvironmentVariable(_validationOutputVariable);
        if (!string.IsNullOrWhiteSpace(reportPath))
        {
            var lines = new List<string>
            {
                $"layer_enabled={renderer.ValidationLayerEnabled}",
                $"device={renderer.DeviceName}",
                $"messages={messages.Count}",
            };
            lines.AddRange(messages.Select(m => $"{m.Severity}: {m.Text}"));
            File.WriteAllLines(reportPath, lines);
        }

        if (!renderer.ValidationLayerEnabled)
        {
            Assert.Skip("VK_LAYER_KHRONOS_validation is not installed on this machine, so the layer did not load.");
        }

        Assert.DoesNotContain(messages, m => m.Severity == "error");
    }

    [Fact]
    public void GPU_and_CPU_frame_times_are_measured_when_requested()
    {
        string? outputPath = Environment.GetEnvironmentVariable(_gpuTimingOutputVariable);
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            Assert.Skip($"Set {_gpuTimingOutputVariable} to a file path to record GPU and CPU frame times.");
        }

        const int nodeCount = 10_000;
        const int warmupFrames = 60;
        const int measuredFrames = 600;
        using VulkanOffscreenRenderer renderer = CreateOrSkip(1280);
        if (!renderer.TimestampsSupported)
        {
            Assert.Skip("This device does not report graphics timestamp queries.");
        }

        var random = new Random(20261116);
        var positions = new float[nodeCount * 2];
        for (int index = 0; index < positions.Length; index++)
        {
            positions[index] = (float)(random.NextDouble() * 2 - 1);
        }

        var styles = new NodeStyleTable(nodeCount, NodeStyle.Create(0.8f, 0.86f, 0.95f, 1f, 0.006f));
        NodeInstanceBuffer instances = Prepare(renderer, positions, styles);

        for (int frame = 0; frame < warmupFrames; frame++)
        {
            renderer.RenderTimed((0.05f, 0.06f, 0.08f, 1f), instances.Bytes, instances.InstanceCount, NodeView.Identity);
        }

        var gpuMs = new double[measuredFrames];
        var cpuMs = new double[measuredFrames];
        var clock = Stopwatch.StartNew();
        for (int frame = 0; frame < measuredFrames; frame++)
        {
            long start = clock.ElapsedTicks;
            gpuMs[frame] = renderer.RenderTimed((0.05f, 0.06f, 0.08f, 1f), instances.Bytes, instances.InstanceCount, NodeView.Identity);
            cpuMs[frame] = (clock.ElapsedTicks - start) * 1000.0 / Stopwatch.Frequency;
        }

        Array.Sort(gpuMs);
        Array.Sort(cpuMs);
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        string gpu50 = Percentile(gpuMs, 0.50).ToString("F4", culture);
        string gpu95 = Percentile(gpuMs, 0.95).ToString("F4", culture);
        string cpu50 = Percentile(cpuMs, 0.50).ToString("F4", culture);
        string cpu95 = Percentile(cpuMs, 0.95).ToString("F4", culture);
        string line = $"device={renderer.DeviceName}; nodes={nodeCount}; target=1280x1280; frames={measuredFrames}; " +
            $"gpu_p50_ms={gpu50}; gpu_p95_ms={gpu95}; cpu_wall_p50_ms={cpu50}; cpu_wall_p95_ms={cpu95} " +
            "(cpu_wall includes submit, wait, and readback copy; not comparable with gpu_* numbers)";
        File.WriteAllText(outputPath, line + Environment.NewLine);
    }

    /// <summary>Nearest-rank percentile over sorted samples.</summary>
    private static double Percentile(double[] sorted, double fraction)
    {
        int rank = (int)Math.Ceiling(fraction * sorted.Length) - 1;
        return sorted[Math.Clamp(rank, 0, sorted.Length - 1)];
    }
}
