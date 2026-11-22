using Bazaar.Constellation.Rendering.Core;

namespace Bazaar.Constellation.Rendering.Tests;

public class RenderSceneTests
{
    [Fact]
    public void Accepts_valid_edges()
    {
        var scene = new RenderScene(3, new[] { 0, 1, 1, 2 });
        Assert.Equal(3, scene.NodeCount);
        Assert.Equal(2, scene.EdgeCount);
    }

    [Fact]
    public void Rejects_odd_edge_lists_out_of_range_endpoints_and_negative_counts()
    {
        Assert.Throws<ArgumentException>(() => new RenderScene(3, new[] { 0, 1, 2 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RenderScene(3, new[] { 0, 3 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RenderScene(-1, default));
    }
}

public class ConstellationSceneAdapterTests
{
    [Fact]
    public void ToRenderScene_keeps_node_count_and_edges()
    {
        int[] edges = [0, 1, 1, 2];
        RenderScene render = new ConstellationScene(3, edges, new[] { 0, 0, 1 }).ToRenderScene();
        Assert.Equal(3, render.NodeCount);
        Assert.Equal(2, render.EdgeCount);
        Assert.True(render.EdgePairs.Span.SequenceEqual(edges));
    }

    [Fact]
    public void ToRenderScene_works_without_edges_or_clusters()
    {
        RenderScene render = new ConstellationScene(2, default, default).ToRenderScene();
        Assert.Equal(2, render.NodeCount);
        Assert.Equal(0, render.EdgeCount);
    }

    [Fact]
    public void SetScene_extension_sends_topology_then_cluster_styles()
    {
        var renderer = new RecordingRenderer();
        NodeStyle fallback = NodeStyle.Create(0.5f, 0.5f, 0.5f, 1f, 0.012f);
        int[] clusters = [0, 1];

        renderer.SetScene(new ConstellationScene(2, new[] { 0, 1 }, clusters), fallback);

        Assert.Equal(2, renderer.Scene!.NodeCount);
        Assert.Equal(1, renderer.Scene.EdgeCount);
        NodeStyle expected = ConstellationNodeStyles.ForCluster(1);
        NodeStyle got = renderer.Styles!.Get(1);
        Assert.Equal(expected.Red, got.Red);
        Assert.Equal(fallback.Radius, got.Radius);
    }

    private sealed class RecordingRenderer : IConstellationRenderer
    {
        public RenderScene? Scene { get; private set; }

        public NodeStyleTable? Styles { get; private set; }

        public RendererCapabilities Capabilities => throw new NotSupportedException();

        public RenderAttachResult Attach(RenderSurface surface) => throw new NotSupportedException();

        public void SetScene(RenderScene scene) => Scene = scene;

        public void SetNodeStyles(NodeStyleTable styles) => Styles = styles;

        public bool SubmitFrame(RenderFrame frame, double nowSeconds) => false;

        public void Resize(int widthPixels, int heightPixels)
        {
        }

        public void Draw(double nowSeconds)
        {
        }

        public void Dispose()
        {
        }
    }
}
