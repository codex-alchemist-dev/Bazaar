using Bazaar.Constellation.Rendering.Core;

namespace Bazaar.Client.GitSolarSystem.Panes;

/// <summary>The camera pose that frames a set of positions: the centre in world units and the zoom in pixels per unit.</summary>
public readonly record struct GitSolarViewFit(float CenterX, float CenterY, float Zoom);

/// <summary>
/// Pure geometry for the solar canvas: orbit radii, the nearest-commit hit test and the view fit. No Avalonia types.
/// Positions are packed x,y pairs (node i reads xy[2i], xy[2i+1]). Branch index 0 is the default branch
/// (<see cref="GitSolarSceneBuilder"/> puts it first), and it forms the core that every orbit circles.
/// </summary>
public static class GitSolarGeometry
{
    /// <summary>The branch index of the default branch in a <see cref="GitSolarScene"/>.</summary>
    public const int DefaultBranchIndex = 0;

    /// <summary>
    /// The core centre: the mean position of the default branch's commits. Returns (0, 0) when that branch has no commits.
    /// </summary>
    public static (float X, float Y) CoreCentre(ReadOnlySpan<float> xy, ReadOnlySpan<int> branchOfNode)
    {
        double sumX = 0;
        double sumY = 0;
        int count = 0;
        int nodes = Math.Min(branchOfNode.Length, xy.Length / 2);
        for (int i = 0; i < nodes; i++)
        {
            if (branchOfNode[i] != DefaultBranchIndex)
            {
                continue;
            }

            sumX += xy[2 * i];
            sumY += xy[(2 * i) + 1];
            count++;
        }

        return count == 0 ? (0f, 0f) : ((float)(sumX / count), (float)(sumY / count));
    }

    /// <summary>
    /// The orbit radius of one branch about the core: the mean distance of that branch's commits from (<paramref name="centreX"/>,
    /// <paramref name="centreY"/>). Returns 0 when the branch has no commits, so the caller can skip its ring.
    /// </summary>
    public static float RingRadius(ReadOnlySpan<float> xy, ReadOnlySpan<int> branchOfNode, int branch, float centreX, float centreY)
    {
        double sum = 0;
        int count = 0;
        int nodes = Math.Min(branchOfNode.Length, xy.Length / 2);
        for (int i = 0; i < nodes; i++)
        {
            if (branchOfNode[i] != branch)
            {
                continue;
            }

            double dx = xy[2 * i] - (double)centreX;
            double dy = xy[(2 * i) + 1] - (double)centreY;
            sum += Math.Sqrt((dx * dx) + (dy * dy));
            count++;
        }

        return count == 0 ? 0f : (float)(sum / count);
    }

    /// <summary>
    /// The node nearest to (<paramref name="x"/>, <paramref name="y"/>) within <paramref name="maxDistance"/> (boundary included),
    /// or -1 when none is in range. Ties go to the lower index. Non-finite positions never match.
    /// </summary>
    public static int NearestNode(ReadOnlySpan<float> xy, float x, float y, float maxDistance)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y) || !(maxDistance >= 0f) || float.IsInfinity(maxDistance))
        {
            return -1;
        }

        double limit = (double)maxDistance * maxDistance;
        int best = -1;
        double bestDistance = double.PositiveInfinity;
        for (int i = 0; i + 1 < xy.Length; i += 2)
        {
            float px = xy[i];
            float py = xy[i + 1];
            if (!float.IsFinite(px) || !float.IsFinite(py))
            {
                continue;
            }

            double dx = px - (double)x;
            double dy = py - (double)y;
            double distance = (dx * dx) + (dy * dy);
            if (distance > limit || distance >= bestDistance)
            {
                continue;
            }

            bestDistance = distance;
            best = i / 2;
        }

        return best;
    }

    /// <summary>
    /// The camera that frames every finite position inside a viewport of <paramref name="widthPixels"/> by
    /// <paramref name="heightPixels"/>, keeping <paramref name="paddingPixels"/> free on each side. An axis with no extent
    /// is ignored; when neither axis has extent (one node, or none) the zoom is 1. The zoom is clamped to the camera range.
    /// </summary>
    public static GitSolarViewFit FitView(ReadOnlySpan<float> xy, int widthPixels, int heightPixels, float paddingPixels)
    {
        double minX = double.PositiveInfinity;
        double minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity;
        double maxY = double.NegativeInfinity;
        for (int i = 0; i + 1 < xy.Length; i += 2)
        {
            float px = xy[i];
            float py = xy[i + 1];
            if (!float.IsFinite(px) || !float.IsFinite(py))
            {
                continue;
            }

            minX = Math.Min(minX, px);
            minY = Math.Min(minY, py);
            maxX = Math.Max(maxX, px);
            maxY = Math.Max(maxY, py);
        }

        if (double.IsPositiveInfinity(minX))
        {
            return new GitSolarViewFit(0f, 0f, 1f);
        }

        double usableX = Math.Max(1, widthPixels) - (2d * paddingPixels);
        double usableY = Math.Max(1, heightPixels) - (2d * paddingPixels);
        double spanX = maxX - minX;
        double spanY = maxY - minY;
        double zoom = double.PositiveInfinity;
        if (spanX > 0)
        {
            zoom = Math.Min(zoom, Math.Max(usableX, 1) / spanX);
        }

        if (spanY > 0)
        {
            zoom = Math.Min(zoom, Math.Max(usableY, 1) / spanY);
        }

        if (double.IsPositiveInfinity(zoom))
        {
            zoom = 1;
        }

        zoom = Math.Clamp(zoom, Camera2D.MinZoom, Camera2D.MaxZoom);
        return new GitSolarViewFit((float)((minX + maxX) / 2), (float)((minY + maxY) / 2), (float)zoom);
    }

    /// <summary>Applies a fit to a camera. The screen centre stays fixed while the zoom changes, then the camera centres on the fit.</summary>
    public static void ApplyFit(Camera2D camera, GitSolarViewFit fit, int widthPixels, int heightPixels)
    {
        ArgumentNullException.ThrowIfNull(camera);
        float factor = fit.Zoom / camera.Zoom;
        camera.ZoomAt(widthPixels * 0.5f, heightPixels * 0.5f, widthPixels, heightPixels, factor);
        camera.CenterOn(fit.CenterX, fit.CenterY);
    }
}
