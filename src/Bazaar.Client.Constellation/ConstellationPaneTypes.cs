namespace Bazaar.Client.Constellation;

/// <summary>A box in graph (layout) coordinates, used as the visible area of the Constellation pane.</summary>
public readonly record struct ConstellationBox(float MinX, float MinY, float MaxX, float MaxY);

/// <summary>One node of a tile frame, positioned in graph coordinates.</summary>
public readonly record struct TileNode(string Id, float X, float Y);
