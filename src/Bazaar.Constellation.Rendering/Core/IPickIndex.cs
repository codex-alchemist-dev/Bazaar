namespace Bazaar.Constellation.Rendering.Core;

/// <summary>Hit-testing over node positions.</summary>
public interface IPickIndex
{
    /// <summary>Returns the index of the nearest node within <paramref name="radius"/> of a point, or -1.</summary>
    int Nearest(float worldX, float worldY, float radius);
}
