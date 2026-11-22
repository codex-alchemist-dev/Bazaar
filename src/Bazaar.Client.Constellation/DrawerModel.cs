namespace Bazaar.Client.Constellation;

/// <summary>What the drawer shows: the node list, an empty area, or the reason no nodes can be listed.</summary>
public enum DrawerStatus
{
    /// <summary>No graph version is known yet (no LOD snapshot). Nothing is requested.</summary>
    NoGraph,

    /// <summary>The graph is above the node cap, so only clusters exist. Nothing is requested.</summary>
    ClustersOnly,

    /// <summary>The graph is known and the box holds no nodes.</summary>
    Empty,

    /// <summary>The list holds the nodes of the newest accepted tile frame.</summary>
    Ready,
}

/// <summary>One listed node: its id and its position in graph (layout) coordinates. The tile carries no label.</summary>
public sealed record DrawerNode(string Id, float X, float Y);

/// <summary>The graph state one refresh needs: the version to ask for, and whether the server lists nodes at all.</summary>
public sealed record DrawerGraphState(long GraphVersion, bool ClustersOnly);
