namespace Bazaar.Constellation.Rendering.Core;

/// <summary>A named drawing layer. Layers with a lower <see cref="Order"/> are drawn first.</summary>
public readonly record struct RenderLayer(string Name, int Order)
{
    /// <summary>Builds a layer. The name must not be null, empty or whitespace.</summary>
    public static RenderLayer Create(string name, int order)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Layer name must not be empty or whitespace.", nameof(name));
        }

        return new RenderLayer(name, order);
    }
}
