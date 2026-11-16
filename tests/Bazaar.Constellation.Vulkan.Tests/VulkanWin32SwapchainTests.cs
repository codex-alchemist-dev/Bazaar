using System.Diagnostics;
using Bazaar.Constellation.Rendering;
using Bazaar.Constellation.Rendering.Core;

namespace Bazaar.Constellation.Vulkan.Tests;

/// <summary>
/// Runs the Vulkan node pipeline on a real Win32 window, so it can run on a physical GPU without a headless extension.
/// Skips on non-Windows hosts and where the loader finds no device.
/// </summary>
public class VulkanWin32SwapchainTests
{
    /// <summary>Environment variable that names a file to receive frame-time results. Unset means the timing test is skipped.</summary>
    private const string _timingOutputVariable = "BAZAAR_VULKAN_FRAME_TIMING_OUT";

    private static void RequireWindowsDevice()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Win32 window surfaces exist only on Windows.");
        }

        VulkanProbeResult probe = VulkanProbe.Run();
        if (!probe.Available)
        {
            Assert.Skip(probe.Reason);
        }
    }

    private static RenderSurface Surface(NativeTestWindow window, int width, int height) =>
        new(RenderSurfaceKind.Win32, window.Instance, window.Handle, width, height);

    [Fact]
    public void Win32_surface_attaches_and_draws_node_instances()
    {
        RequireWindowsDevice();
        using NativeTestWindow window = NativeTestWindow.Create(320, 200);
        using var renderer = new VulkanConstellationRenderer();

        RenderAttachResult attach = renderer.Attach(Surface(window, 320, 200));
        Assert.True(attach.Attached, attach.Reason);

        renderer.SetScene(new ConstellationScene(3, default, default));
        Assert.True(renderer.SubmitFrame(new RenderFrame(1, 1, 2, new float[] { -0.5f, -0.5f, 0f, 0f, 0.5f, 0.5f }), 0));
        for (int tick = 0; tick < 10; tick++)
        {
            renderer.Draw(tick / 60.0);
        }
    }

    [Fact]
    public void Win32_surface_resizes_and_keeps_drawing()
    {
        RequireWindowsDevice();
        using NativeTestWindow window = NativeTestWindow.Create(320, 200);
        using var renderer = new VulkanConstellationRenderer();
        Assert.True(renderer.Attach(Surface(window, 320, 200)).Attached);

        renderer.SetScene(new ConstellationScene(2, default, default));
        Assert.True(renderer.SubmitFrame(new RenderFrame(1, 1, 2, new float[] { -1f, 0f, 1f, 0f }), 0));
        renderer.Resize(640, 480);
        renderer.Draw(0);
        renderer.Resize(128, 96);
        renderer.Draw(0.1);
    }

    [Fact]
    public void Win32_frame_times_are_measured_when_requested()
    {
        RequireWindowsDevice();
        string? outputPath = Environment.GetEnvironmentVariable(_timingOutputVariable);
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            Assert.Skip($"Set {_timingOutputVariable} to a file path to record frame times.");
        }

        const int nodeCount = 10_000;
        const int warmupFrames = 60;
        const int measuredFrames = 600;
        const int width = 1280;
        const int height = 720;

        using NativeTestWindow window = NativeTestWindow.Create(width, height);
        using var renderer = new VulkanConstellationRenderer();
        Assert.True(renderer.Attach(Surface(window, width, height)).Attached);

        renderer.SetScene(new ConstellationScene(nodeCount, default, default));
        var random = new Random(20261116);
        var positions = new float[nodeCount * 2];
        for (int index = 0; index < positions.Length; index++)
        {
            positions[index] = (float)(random.NextDouble() * 2 - 1);
        }

        Assert.True(renderer.SubmitFrame(new RenderFrame(1, 1, 2, positions), 0));
        for (int frame = 0; frame < warmupFrames; frame++)
        {
            renderer.Draw(0);
        }

        var samples = new double[measuredFrames];
        var clock = Stopwatch.StartNew();
        for (int frame = 0; frame < measuredFrames; frame++)
        {
            long start = clock.ElapsedTicks;
            renderer.Draw(0);
            samples[frame] = (clock.ElapsedTicks - start) * 1000.0 / Stopwatch.Frequency;
        }

        Array.Sort(samples);
        double p50 = Percentile(samples, 0.50);
        double p95 = Percentile(samples, 0.95);
        IReadOnlyList<string> devices = renderer.Capabilities.Devices;
        string device = devices.Count > 0 ? devices[0] : "unknown";
        string line = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"device={device}; nodes={nodeCount}; window={width}x{height}; frames={measuredFrames}; present=Fifo; p50_ms={p50:F3}; p95_ms={p95:F3}; mean_ms={samples.Average():F3}");
        File.WriteAllText(outputPath, line + Environment.NewLine);
    }

    /// <summary>Nearest-rank percentile over sorted samples.</summary>
    private static double Percentile(double[] sorted, double fraction)
    {
        int rank = (int)Math.Ceiling(fraction * sorted.Length) - 1;
        return sorted[Math.Clamp(rank, 0, sorted.Length - 1)];
    }
}
