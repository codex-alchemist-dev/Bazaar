namespace Bazaar.Client.Core.Constellation;

/// <summary>
/// Uniform spatial grid over packed x,y node positions (index i reads xy[2i], xy[2i+1]), used for hit-testing.
/// Rebuilt per frame from a span; the buffers are reused between rebuilds. Nodes with a NaN or infinite coordinate
/// are left out of the grid, so they never match a query. Queries visit only the cells that overlap the search area.
/// Pure logic, no Avalonia. Not thread-safe.
/// </summary>
public sealed class SpatialGrid
{
    // Upper bound on cells per rebuild, relative to node count. Keeps sparse layouts from allocating huge grids.
    private const int _minCellBudget = 1024;
    private const int _cellBudgetPerNode = 4;

    private readonly float _requestedCellSize;
    private float[] _xy = [];
    private int[] _cellOf = [];
    private int[] _cellStart = [];
    private int[] _cellCursor = [];
    private int[] _cellItems = [];
    private double _minX;
    private double _minY;
    private double _cellSize;
    private int _columns;
    private int _rows;

    /// <summary>Creates a grid. <paramref name="cellSize"/> is the requested cell edge in layout units.</summary>
    public SpatialGrid(float cellSize)
    {
        if (!float.IsFinite(cellSize) || cellSize <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(cellSize), "Cell size must be a positive, finite number.");
        }

        _requestedCellSize = cellSize;
        _cellSize = cellSize;
    }

    /// <summary>Cell edge in use after the last rebuild. Grows past the requested size when the cell budget requires it.</summary>
    public double CellSize => _cellSize;

    /// <summary>Columns in the last rebuild (0 when no node had finite coordinates).</summary>
    public int Columns => _columns;

    /// <summary>Rows in the last rebuild (0 when no node had finite coordinates).</summary>
    public int Rows => _rows;

    /// <summary>Node slots in the last rebuild, including nodes excluded for non-finite coordinates.</summary>
    public int NodeCount { get; private set; }

    /// <summary>Rebuilds the grid from packed x,y positions. Copies the input, so the span need not outlive the call.</summary>
    /// <exception cref="ArgumentException">The span has an odd length (it must hold x,y pairs).</exception>
    public void Rebuild(ReadOnlySpan<float> xy)
    {
        if ((xy.Length & 1) != 0)
        {
            throw new ArgumentException("Packed positions need an even length (x,y pairs).", nameof(xy));
        }

        int nodes = xy.Length / 2;
        EnsureLength(ref _xy, xy.Length);
        xy.CopyTo(_xy.AsSpan(0, xy.Length));
        NodeCount = nodes;

        double minX = double.PositiveInfinity;
        double minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity;
        double maxY = double.NegativeInfinity;
        int finite = 0;
        for (int i = 0; i < nodes; i++)
        {
            float x = xy[2 * i];
            float y = xy[(2 * i) + 1];
            if (!float.IsFinite(x) || !float.IsFinite(y))
            {
                continue;
            }

            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
            finite++;
        }

        if (finite == 0)
        {
            _columns = 0;
            _rows = 0;
            return;
        }

        // Grow the cell edge until the grid fits the budget. Doubles keep the arithmetic from overflowing.
        double cell = _requestedCellSize;
        double spanX = maxX - minX;
        double spanY = maxY - minY;
        double budget = Math.Max(_minCellBudget, (double)_cellBudgetPerNode * finite);
        double columns;
        double rows;
        while (true)
        {
            columns = Math.Floor(spanX / cell) + 1;
            rows = Math.Floor(spanY / cell) + 1;
            if (columns * rows <= budget)
            {
                break;
            }

            cell *= 2;
        }

        _columns = (int)columns;
        _rows = (int)rows;
        _cellSize = cell;
        _minX = minX;
        _minY = minY;

        int cellCount = _columns * _rows;
        EnsureLength(ref _cellOf, nodes);
        EnsureLength(ref _cellStart, cellCount + 1);
        EnsureLength(ref _cellCursor, cellCount);
        EnsureLength(ref _cellItems, finite);
        Array.Clear(_cellStart, 0, cellCount + 1);

        // Counting sort: count per cell, prefix-sum to offsets, then place node indices.
        for (int i = 0; i < nodes; i++)
        {
            float x = xy[2 * i];
            float y = xy[(2 * i) + 1];
            if (!float.IsFinite(x) || !float.IsFinite(y))
            {
                _cellOf[i] = -1;
                continue;
            }

            int cellIndex = (CellRow(y) * _columns) + CellColumn(x);
            _cellOf[i] = cellIndex;
            _cellStart[cellIndex + 1]++;
        }

        for (int c = 0; c < cellCount; c++)
        {
            _cellStart[c + 1] += _cellStart[c];
        }

        Array.Copy(_cellStart, _cellCursor, cellCount);
        for (int i = 0; i < nodes; i++)
        {
            int c = _cellOf[i];
            if (c >= 0)
            {
                _cellItems[_cellCursor[c]++] = i;
            }
        }
    }

    /// <summary>
    /// Writes the indices of nodes whose centre is within <paramref name="radius"/> of (<paramref name="x"/>, <paramref name="y"/>),
    /// boundary included. Returns the total number of matches, which may exceed <paramref name="results"/>.Length; only the
    /// first results.Length matches are written. Match order is by cell, not by index. Returns 0 for a NaN, infinite
    /// or negative radius, or a NaN query point.
    /// </summary>
    public int QueryRadius(float x, float y, float radius, Span<int> results)
    {
        if (_columns == 0 || !float.IsFinite(x) || !float.IsFinite(y) || !(radius >= 0f) || float.IsNaN(radius))
        {
            return 0;
        }

        double r = radius;
        double r2 = r * r;
        int found = 0;
        int x0 = CellColumn(x - r);
        int x1 = CellColumn(x + r);
        int y0 = CellRow(y - r);
        int y1 = CellRow(y + r);
        for (int row = y0; row <= y1; row++)
        {
            for (int col = x0; col <= x1; col++)
            {
                int cell = (row * _columns) + col;
                for (int k = _cellStart[cell]; k < _cellStart[cell + 1]; k++)
                {
                    int node = _cellItems[k];
                    double dx = _xy[2 * node] - (double)x;
                    double dy = _xy[(2 * node) + 1] - (double)y;
                    if ((dx * dx) + (dy * dy) <= r2)
                    {
                        if (found < results.Length)
                        {
                            results[found] = node;
                        }

                        found++;
                    }
                }
            }
        }

        return found;
    }

    /// <summary>
    /// Returns the index of the nearest node within <paramref name="maxDistance"/> of (<paramref name="x"/>, <paramref name="y"/>),
    /// or -1 when none is in range. Ties go to the lower index. Visits only cells that overlap the search square.
    /// </summary>
    public int NearestNode(float x, float y, float maxDistance)
    {
        if (_columns == 0 || !float.IsFinite(x) || !float.IsFinite(y) || !(maxDistance >= 0f) || float.IsNaN(maxDistance))
        {
            return -1;
        }

        double r = maxDistance;
        double r2 = r * r;
        int best = -1;
        double bestD2 = double.PositiveInfinity;
        int x0 = CellColumn(x - r);
        int x1 = CellColumn(x + r);
        int y0 = CellRow(y - r);
        int y1 = CellRow(y + r);
        for (int row = y0; row <= y1; row++)
        {
            for (int col = x0; col <= x1; col++)
            {
                int cell = (row * _columns) + col;
                for (int k = _cellStart[cell]; k < _cellStart[cell + 1]; k++)
                {
                    int node = _cellItems[k];
                    double dx = _xy[2 * node] - (double)x;
                    double dy = _xy[(2 * node) + 1] - (double)y;
                    double d2 = (dx * dx) + (dy * dy);
                    if (d2 > r2)
                    {
                        continue;
                    }

                    if (d2 < bestD2 || (d2 == bestD2 && node < best))
                    {
                        best = node;
                        bestD2 = d2;
                    }
                }
            }
        }

        return best;
    }

    private int CellColumn(double x) => ClampCell(Math.Floor((x - _minX) / _cellSize), _columns);

    private int CellRow(double y) => ClampCell(Math.Floor((y - _minY) / _cellSize), _rows);

    private static int ClampCell(double index, int count)
    {
        if (index <= 0)
        {
            return 0;
        }

        if (index >= count - 1)
        {
            return count - 1;
        }

        return (int)index;
    }

    private static void EnsureLength<T>(ref T[] buffer, int length)
    {
        if (buffer.Length < length)
        {
            buffer = new T[Math.Max(length, buffer.Length * 2)];
        }
    }
}
