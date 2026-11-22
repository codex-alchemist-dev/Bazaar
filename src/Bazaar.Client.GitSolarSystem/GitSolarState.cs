using Bazaar.Client.GitSolarSystem.Abstractions;
using Bazaar.Constellation.Rendering.Core;
using Bazaar.Contracts.Graph;

namespace Bazaar.Client.GitSolarSystem;

/// <summary>
/// The client-side state of one Git Solar System view: the current scene, the latest layout frame, and whether the view is stale.
/// Pure logic. <see cref="Apply"/> never throws for bad server data; it refuses the update and returns false.
/// </summary>
public sealed class GitSolarState
{
    private readonly FrameSequencer _sequencer = new();

    /// <summary>The current scene, or null before the first accepted snapshot.</summary>
    public GitSolarScene? Scene { get; private set; }

    /// <summary>The last accepted layout frame, positions packed x,y (dimensions 2). Null when none applies to the current scene.</summary>
    public RenderFrame? LatestFrame { get; private set; }

    /// <summary>True after a disconnect, until the next accepted snapshot or frame.</summary>
    public bool IsStale { get; private set; }

    /// <summary>Applies one server update. Returns true when it changed the state.</summary>
    public bool Apply(GitSolarUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        switch (update)
        {
            case GitSolarUpdate.Snapshot snapshot:
                return ApplySnapshot(snapshot.Value);
            case GitSolarUpdate.Frame frame:
                return ApplyFrame(frame.Value);
            case GitSolarUpdate.Disconnected:
                bool wasStale = IsStale;
                IsStale = true;
                return !wasStale;
            default:
                return false;
        }
    }

    private bool ApplySnapshot(GitSolarSnapshot snapshot)
    {
        if (Scene is { } current && snapshot.GraphVersion < current.GraphVersion)
        {
            return false;
        }

        GitSolarScene next;
        try
        {
            next = GitSolarSceneBuilder.Build(snapshot);
        }
        catch (ArgumentException)
        {
            return false;
        }

        if (Scene is null || Scene.GraphVersion != next.GraphVersion)
        {
            _sequencer.Reset();
            LatestFrame = null;
        }
        else if (LatestFrame is { } frame && frame.NodeCount != next.Shas.Count)
        {
            LatestFrame = null;
        }

        Scene = next;
        IsStale = false;
        return true;
    }

    private bool ApplyFrame(LayoutFrame frame)
    {
        if (Scene is not { } scene || frame.GraphVersion != scene.GraphVersion)
        {
            return false;
        }

        int nodeCount = scene.Shas.Count;
        ReadOnlySpan<float> xyz = frame.Xyz.Span;
        if (xyz.Length != 3 * nodeCount)
        {
            return false;
        }

        var positions = new float[2 * nodeCount];
        for (int i = 0; i < nodeCount; i++)
        {
            float x = xyz[3 * i];
            float y = xyz[(3 * i) + 1];
            if (!float.IsFinite(x) || !float.IsFinite(y))
            {
                return false;
            }

            positions[2 * i] = x;
            positions[(2 * i) + 1] = y;
        }

        var render = new RenderFrame(scene.GraphVersion, frame.LayoutSeq, 2, positions);
        if (!_sequencer.TryAccept(render))
        {
            return false;
        }

        LatestFrame = render;
        IsStale = false;
        return true;
    }
}
