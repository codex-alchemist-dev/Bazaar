namespace Bazaar.Constellation.Rendering;

/// <summary>
/// Thin boundary between the Constellation view and a drawing backend (doc 19, WP7).
/// The server computes layout and clustering; a backend only draws the frames it is given.
/// </summary>
public interface IConstellationRenderer : IDisposable
{
    /// <summary>What this backend can do on this machine. Valid after the backend has probed.</summary>
    RendererCapabilities Capabilities { get; }

    /// <summary>Binds the backend to a native window. Returns false with a reason when it cannot draw.</summary>
    RenderAttachResult Attach(RenderSurface surface);

    /// <summary>Replaces the topology. Interpolation restarts from the first frame of the new scene.</summary>
    void SetScene(ConstellationScene scene);

    /// <summary>Offers a layout frame. Returns false when it is not newer than the last frame applied.</summary>
    bool SubmitFrame(RenderFrame frame, double nowSeconds);

    /// <summary>Tells the backend the surface size in pixels.</summary>
    void Resize(int widthPixels, int heightPixels);

    /// <summary>Draws the interpolated state at a monotonic time in seconds.</summary>
    void Draw(double nowSeconds);
}
