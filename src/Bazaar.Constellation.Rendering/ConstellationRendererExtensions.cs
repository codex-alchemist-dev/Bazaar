using Bazaar.Constellation.Rendering.Core;

namespace Bazaar.Constellation.Rendering;

/// <summary>Adapter from the Bazaar scene to the neutral renderer contract.</summary>
public static class ConstellationRendererExtensions
{
    /// <summary>
    /// Hands the scene's topology to the backend and colours each node by its cluster
    /// (<paramref name="fallback"/> when the scene has no clusters), keeping the fallback's radius and ring width.
    /// </summary>
    public static void SetScene(this IConstellationRenderer renderer, ConstellationScene scene, NodeStyle fallback)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(scene);
        renderer.SetScene(scene.ToRenderScene());
        renderer.SetNodeStyles(ConstellationNodeStyles.BuildColoured(scene, fallback));
    }
}
