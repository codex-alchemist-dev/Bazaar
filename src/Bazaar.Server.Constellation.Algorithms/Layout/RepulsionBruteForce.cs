namespace Bazaar.Server.Constellation.Layout;

/// <summary>Exact O(n^2) repulsion. The reference that Barnes-Hut with theta 0 must match.</summary>
public static class RepulsionBruteForce
{
    /// <summary>Adds the exact repulsion on every body into <paramref name="forces"/> (packed x,y,z, stride 3).</summary>
    public static void Accumulate(double[] xyz, double[] mass, double scalingRatio, double[] forces)
    {
        ArgumentNullException.ThrowIfNull(xyz);
        ArgumentNullException.ThrowIfNull(mass);
        ArgumentNullException.ThrowIfNull(forces);

        for (var i = 0; i < mass.Length; i++)
        {
            var acc = new BarnesHutTree.Accumulator();
            for (var j = 0; j < mass.Length; j++)
            {
                if (j != i)
                {
                    BarnesHutTree.Repel(
                        xyz[3 * i], xyz[(3 * i) + 1], xyz[(3 * i) + 2], mass[i],
                        xyz[3 * j], xyz[(3 * j) + 1], xyz[(3 * j) + 2], mass[j],
                        scalingRatio, ref acc);
                }
            }

            forces[3 * i] += acc.X;
            forces[(3 * i) + 1] += acc.Y;
            forces[(3 * i) + 2] += acc.Z;
        }
    }
}
