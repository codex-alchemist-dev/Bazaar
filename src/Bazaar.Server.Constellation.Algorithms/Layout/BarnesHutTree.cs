namespace Bazaar.Server.Constellation.Layout;

/// <summary>
/// Octree for Barnes-Hut repulsion (a quadtree in 2D: with z = 0 every body stays in the lower half of the z split).
/// A distant cell whose size over distance is below theta acts as one body at its centre of mass. Positions are
/// packed x,y,z per body (stride 3). The tree is rebuilt for every tick and its storage is reused.
/// </summary>
public sealed class BarnesHutTree
{
    private const int _maxDepth = 32;
    private const int _childCount = 8;
    internal const double _repulsionMinDistanceSquared = 1e-12;

    private Cell[] _cells = new Cell[64];
    private int _cellCount;
    private int[] _next = [];

    private struct Cell
    {
        public double CenterX, CenterY, CenterZ, Half;
        public double Mass, WeightedX, WeightedY, WeightedZ;
        public int FirstChild; // -1 for a leaf; children are FirstChild + 0..7
        public int Head;       // first body of a leaf, -1 when empty
    }

    /// <summary>Force accumulator for one body.</summary>
    internal struct Accumulator
    {
        public double X, Y, Z;
    }

    /// <summary>Builds the tree over packed positions (stride 3) and masses (one per body).</summary>
    public void Build(double[] xyz, double[] mass)
    {
        ArgumentNullException.ThrowIfNull(xyz);
        ArgumentNullException.ThrowIfNull(mass);

        var count = mass.Length;
        _cellCount = 0;
        if (count == 0)
        {
            return;
        }

        var min = new double[3] { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity };
        var max = new double[3] { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
        for (var i = 0; i < count; i++)
        {
            for (var axis = 0; axis < 3; axis++)
            {
                var value = xyz[(3 * i) + axis];
                min[axis] = Math.Min(min[axis], value);
                max[axis] = Math.Max(max[axis], value);
            }
        }

        var extent = Math.Max(max[0] - min[0], Math.Max(max[1] - min[1], max[2] - min[2]));
        var half = extent > 0 ? extent / 2 : 1;
        NewCell((min[0] + max[0]) / 2, (min[1] + max[1]) / 2, (min[2] + max[2]) / 2, half);

        if (_next.Length < count)
        {
            _next = new int[count];
        }

        for (var i = 0; i < count; i++)
        {
            Insert(i, xyz, mass);
        }
    }

    /// <summary>Adds the repulsion on every body from all others (approximated per theta) into <paramref name="forces"/>.</summary>
    public void AccumulateRepulsion(double[] xyz, double[] mass, double scalingRatio, double theta, double[] forces)
    {
        ArgumentNullException.ThrowIfNull(forces);
        if (_cellCount == 0)
        {
            return;
        }

        var theta2 = theta * theta;
        for (var i = 0; i < mass.Length; i++)
        {
            var acc = new Accumulator();
            Visit(0, i, xyz, mass, scalingRatio, theta2, ref acc);
            forces[3 * i] += acc.X;
            forces[(3 * i) + 1] += acc.Y;
            forces[(3 * i) + 2] += acc.Z;
        }
    }

    /// <summary>Adds the pairwise repulsion of a point of mass <paramref name="mi"/> from a source of mass <paramref name="mj"/>.</summary>
    internal static void Repel(double px, double py, double pz, double mi, double qx, double qy, double qz, double mj, double scalingRatio, ref Accumulator acc)
    {
        var dx = px - qx;
        var dy = py - qy;
        var dz = pz - qz;
        var d2 = (dx * dx) + (dy * dy) + (dz * dz);
        if (d2 < _repulsionMinDistanceSquared)
        {
            return;
        }

        // Magnitude scalingRatio * mi * mj / d, along the unit vector (dx, dy, dz) / d.
        var f = scalingRatio * mi * mj / d2;
        acc.X += dx * f;
        acc.Y += dy * f;
        acc.Z += dz * f;
    }

    private void Visit(int cell, int body, double[] xyz, double[] mass, double kr, double theta2, ref Accumulator acc)
    {
        var c = _cells[cell];
        if (c.Mass <= 0)
        {
            return;
        }

        var px = xyz[3 * body];
        var py = xyz[(3 * body) + 1];
        var pz = xyz[(3 * body) + 2];

        if (c.FirstChild < 0)
        {
            for (var b = c.Head; b >= 0; b = _next[b])
            {
                if (b != body)
                {
                    Repel(px, py, pz, mass[body], xyz[3 * b], xyz[(3 * b) + 1], xyz[(3 * b) + 2], mass[b], kr, ref acc);
                }
            }

            return;
        }

        // A cell that contains the body is never approximated, so the body does not repel itself.
        var contains = Math.Abs(px - c.CenterX) <= c.Half && Math.Abs(py - c.CenterY) <= c.Half && Math.Abs(pz - c.CenterZ) <= c.Half;
        if (!contains)
        {
            var cx = c.WeightedX / c.Mass;
            var cy = c.WeightedY / c.Mass;
            var cz = c.WeightedZ / c.Mass;
            var dx = px - cx;
            var dy = py - cy;
            var dz = pz - cz;
            var d2 = (dx * dx) + (dy * dy) + (dz * dz);
            var size = 2 * c.Half;
            if (size * size < theta2 * d2)
            {
                Repel(px, py, pz, mass[body], cx, cy, cz, c.Mass, kr, ref acc);
                return;
            }
        }

        for (var k = 0; k < _childCount; k++)
        {
            Visit(c.FirstChild + k, body, xyz, mass, kr, theta2, ref acc);
        }
    }

    private void Insert(int body, double[] xyz, double[] mass)
    {
        var x = xyz[3 * body];
        var y = xyz[(3 * body) + 1];
        var z = xyz[(3 * body) + 2];
        var m = mass[body];

        var cell = 0;
        var depth = 0;
        AddMass(cell, x, y, z, m);
        while (true)
        {
            if (_cells[cell].FirstChild >= 0)
            {
                cell = ChildFor(cell, x, y, z);
                depth++;
                AddMass(cell, x, y, z, m);
                continue;
            }

            var head = _cells[cell].Head;
            if (head < 0)
            {
                _cells[cell].Head = body;
                _next[body] = -1;
                return;
            }

            if (depth >= _maxDepth)
            {
                // Coincident bodies at the depth limit share one leaf as a list.
                _next[body] = head;
                _cells[cell].Head = body;
                return;
            }

            // Split: the resident body moves down. The loop then descends for the new body.
            _cells[cell].Head = -1;
            Subdivide(cell);
            var hx = xyz[3 * head];
            var hy = xyz[(3 * head) + 1];
            var hz = xyz[(3 * head) + 2];
            var child = ChildFor(cell, hx, hy, hz);
            AddMass(child, hx, hy, hz, mass[head]);
            _cells[child].Head = head;
            _next[head] = -1;
        }
    }

    private void AddMass(int cell, double x, double y, double z, double m)
    {
        _cells[cell].Mass += m;
        _cells[cell].WeightedX += m * x;
        _cells[cell].WeightedY += m * y;
        _cells[cell].WeightedZ += m * z;
    }

    private int ChildFor(int cell, double x, double y, double z)
    {
        var c = _cells[cell];
        return c.FirstChild + (x >= c.CenterX ? 1 : 0) + (y >= c.CenterY ? 2 : 0) + (z >= c.CenterZ ? 4 : 0);
    }

    private void Subdivide(int cell)
    {
        var c = _cells[cell];
        var h = c.Half / 2;
        EnsureCapacity(_childCount);
        var first = _cellCount;
        for (var k = 0; k < _childCount; k++)
        {
            NewCell(
                c.CenterX + ((k & 1) != 0 ? h : -h),
                c.CenterY + ((k & 2) != 0 ? h : -h),
                c.CenterZ + ((k & 4) != 0 ? h : -h),
                h);
        }

        _cells[cell].FirstChild = first;
    }

    private int NewCell(double cx, double cy, double cz, double half)
    {
        EnsureCapacity(1);
        var index = _cellCount++;
        _cells[index] = new Cell
        {
            CenterX = cx,
            CenterY = cy,
            CenterZ = cz,
            Half = half,
            FirstChild = -1,
            Head = -1,
        };
        return index;
    }

    private void EnsureCapacity(int extra)
    {
        if (_cellCount + extra > _cells.Length)
        {
            Array.Resize(ref _cells, Math.Max(_cells.Length * 2, _cellCount + extra));
        }
    }
}
