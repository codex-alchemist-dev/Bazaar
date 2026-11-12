namespace Bazaar.Constellation.Rendering.Core;

/// <summary>
/// Uniform grid for pointer hit-testing. Rebuilt from each frame's positions; queries visit only the cells
/// that a search radius touches, never every node.
/// </summary>
public sealed class SpatialGrid : IPickIndex
{
    private readonly float _cellSize;
    private readonly Dictionary<(int Col, int Row), List<int>> _cells = [];
    private float[] _positions = [];
    private int _dimensions = 2;

    public SpatialGrid(float cellSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cellSize);
        _cellSize = cellSize;
    }

    public int NodeCount => _positions.Length / _dimensions;

    /// <summary>Rebuilds the grid. Only x and y are indexed; z is ignored for 3D frames.</summary>
    public void Build(ReadOnlySpan<float> positions, int dimensions)
    {
        if (dimensions is not (2 or 3))
        {
            throw new ArgumentOutOfRangeException(nameof(dimensions), dimensions, "Dimensions must be 2 or 3.");
        }

        _cells.Clear();
        _dimensions = dimensions;
        _positions = positions.ToArray();
        for (int node = 0; node < NodeCount; node++)
        {
            (int col, int row) = CellOf(_positions[node * dimensions], _positions[(node * dimensions) + 1]);
            if (!_cells.TryGetValue((col, row), out List<int>? bucket))
            {
                bucket = [];
                _cells[(col, row)] = bucket;
            }

            bucket.Add(node);
        }
    }

    /// <summary>Returns the nearest node within <paramref name="radius"/> of a world point, or -1.</summary>
    public int Nearest(float worldX, float worldY, float radius)
    {
        if (!float.IsFinite(radius) || radius < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(radius), radius, "Radius must be finite and not negative.");
        }

        (int centerCol, int centerRow) = CellOf(worldX, worldY);
        int reach = (int)MathF.Ceiling(radius / _cellSize);
        int best = -1;
        float bestDistanceSquared = radius * radius;
        for (int row = centerRow - reach; row <= centerRow + reach; row++)
        {
            for (int col = centerCol - reach; col <= centerCol + reach; col++)
            {
                if (!_cells.TryGetValue((col, row), out List<int>? bucket))
                {
                    continue;
                }

                foreach (int node in bucket)
                {
                    float dx = _positions[node * _dimensions] - worldX;
                    float dy = _positions[(node * _dimensions) + 1] - worldY;
                    float distanceSquared = (dx * dx) + (dy * dy);
                    if (distanceSquared <= bestDistanceSquared && (best < 0 || distanceSquared < bestDistanceSquared))
                    {
                        best = node;
                        bestDistanceSquared = distanceSquared;
                    }
                }
            }
        }

        return best;
    }

    private (int Col, int Row) CellOf(float x, float y) =>
        ((int)MathF.Floor(x / _cellSize), (int)MathF.Floor(y / _cellSize));
}
