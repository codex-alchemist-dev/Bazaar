using Bazaar.Client.Constellation;
using Bazaar.Constellation.Rendering;
using Bazaar.Constellation.Rendering.Core;

namespace Bazaar.Client.Constellation.Tests;

public class ConstellationRenderLoopTests
{
    [Fact]
    public void Attach_passes_a_win32_surface_and_marks_the_loop_attached()
    {
        var renderer = new FakeRenderer { AttachAccepted = true };
        using var loop = new ConstellationRenderLoop(renderer);

        RenderAttachResult result = loop.AttachWindow(platformWindow: 0x1234, platformDisplay: 0x5678, widthPixels: 640, heightPixels: 480);

        Assert.True(result.Attached);
        Assert.True(loop.IsAttached);
        Assert.Equal(RenderSurfaceKind.Win32, renderer.LastSurface!.Value.Kind);
        Assert.Equal(0x1234, renderer.LastSurface!.Value.PlatformWindow);
        Assert.Equal(0x5678, renderer.LastSurface!.Value.PlatformDisplay);
        Assert.Equal(640, renderer.LastSurface!.Value.WidthPixels);
        Assert.Equal(480, renderer.LastSurface!.Value.HeightPixels);
    }

    [Fact]
    public void A_refused_attach_leaves_the_loop_detached_and_returns_the_reason()
    {
        var renderer = new FakeRenderer { AttachAccepted = false, AttachReason = "no device" };
        using var loop = new ConstellationRenderLoop(renderer);

        RenderAttachResult result = loop.AttachWindow(1, 1, 10, 10);

        Assert.False(result.Attached);
        Assert.Equal("no device", result.Reason);
        Assert.False(loop.IsAttached);
    }

    [Fact]
    public void Tick_draws_with_the_loop_clock_only_when_attached()
    {
        var renderer = new FakeRenderer { AttachAccepted = true };
        double now = 1.5;
        using var loop = new ConstellationRenderLoop(renderer, clock: () => now);

        loop.Tick();
        Assert.Empty(renderer.DrawTimes);

        loop.AttachWindow(1, 1, 10, 10);
        loop.Tick();
        now = 2.5;
        loop.Tick();

        Assert.Equal(2, renderer.DrawTimes.Count);
        Assert.Equal(1.5, renderer.DrawTimes[0]);
        Assert.Equal(2.5, renderer.DrawTimes[1]);
    }

    [Fact]
    public void Resize_ignores_non_positive_sizes_and_forwards_valid_ones()
    {
        var renderer = new FakeRenderer();
        using var loop = new ConstellationRenderLoop(renderer);

        loop.Resize(0, 480);
        loop.Resize(640, -1);
        Assert.Empty(renderer.Resizes);

        loop.Resize(800, 600);
        Assert.Single(renderer.Resizes);
        Assert.Equal((800, 600), renderer.Resizes[0]);
    }

    [Fact]
    public void Submit_forwards_the_frame_with_the_loop_clock()
    {
        var renderer = new FakeRenderer { FrameAccepted = true };
        using var loop = new ConstellationRenderLoop(renderer, clock: () => 7.0);
        float[] positions = new float[] { 0f, 0f };
        var frame = new RenderFrame(1, 1, 2, positions);

        bool accepted = loop.Submit(frame);

        Assert.True(accepted);
        Assert.Equal(7.0, renderer.LastFrameTime);
    }

    [Fact]
    public void Dispose_detaches_and_only_disposes_an_owned_renderer()
    {
        var kept = new FakeRenderer { AttachAccepted = true };
        var loop = new ConstellationRenderLoop(kept, ownsRenderer: false);
        loop.AttachWindow(1, 1, 10, 10);
        loop.Dispose();
        Assert.False(loop.IsAttached);
        Assert.False(kept.Disposed);

        var owned = new FakeRenderer();
        new ConstellationRenderLoop(owned, ownsRenderer: true).Dispose();
        Assert.True(owned.Disposed);
    }

    [Fact]
    public void Methods_throw_after_dispose()
    {
        var loop = new ConstellationRenderLoop(new FakeRenderer());
        loop.Dispose();

        Assert.Throws<ObjectDisposedException>(() => loop.Tick());
        Assert.Throws<ObjectDisposedException>(() => loop.Resize(10, 10));
    }

    private sealed class FakeRenderer : IConstellationRenderer
    {
        public bool AttachAccepted { get; init; }
        public string AttachReason { get; init; } = string.Empty;
        public bool FrameAccepted { get; init; }
        public RenderSurface? LastSurface { get; private set; }
        public List<double> DrawTimes { get; } = [];
        public List<(int, int)> Resizes { get; } = [];
        public double LastFrameTime { get; private set; }
        public bool Disposed { get; private set; }

        public RendererCapabilities Capabilities => new("fake", "0", [], []);

        public RenderAttachResult Attach(RenderSurface surface)
        {
            LastSurface = surface;
            return new RenderAttachResult(AttachAccepted, AttachReason);
        }

        public void SetScene(RenderScene scene)
        {
        }

        public void SetNodeStyles(NodeStyleTable styles)
        {
        }

        public bool SubmitFrame(RenderFrame frame, double nowSeconds)
        {
            LastFrameTime = nowSeconds;
            return FrameAccepted;
        }

        public void Resize(int widthPixels, int heightPixels) => Resizes.Add((widthPixels, heightPixels));

        public void Draw(double nowSeconds) => DrawTimes.Add(nowSeconds);

        public void Dispose() => Disposed = true;
    }
}
