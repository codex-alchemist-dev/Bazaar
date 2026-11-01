using Bazaar.Server.Constellation.Layout;

namespace Bazaar.Server.Tests.Constellation.Layout;

public class BarnesHutTests
{
    private static (double[] Xyz, double[] Mass) RandomBodies(int count, int seed, bool threeDimensional)
    {
        var random = new Random(seed);
        var xyz = new double[3 * count];
        var mass = new double[count];
        for (var i = 0; i < count; i++)
        {
            xyz[3 * i] = (random.NextDouble() * 200) - 100;
            xyz[(3 * i) + 1] = (random.NextDouble() * 200) - 100;
            xyz[(3 * i) + 2] = threeDimensional ? (random.NextDouble() * 200) - 100 : 0;
            mass[i] = 1 + random.Next(4);
        }

        return (xyz, mass);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Theta_zero_matches_brute_force_on_50_random_nodes(bool threeDimensional)
    {
        var (xyz, mass) = RandomBodies(50, seed: 7, threeDimensional);
        var exact = new double[xyz.Length];
        var approximate = new double[xyz.Length];

        RepulsionBruteForce.Accumulate(xyz, mass, 10, exact);
        var tree = new BarnesHutTree();
        tree.Build(xyz, mass);
        tree.AccumulateRepulsion(xyz, mass, 10, 0, approximate);

        var scale = exact.Max(Math.Abs);
        for (var i = 0; i < exact.Length; i++)
        {
            Assert.True(Math.Abs(exact[i] - approximate[i]) <= 1e-9 * scale, $"Component {i}: brute {exact[i]}, tree {approximate[i]}.");
        }
    }

    [Fact]
    public void Theta_point_eight_stays_close_to_brute_force()
    {
        var (xyz, mass) = RandomBodies(1000, seed: 11, threeDimensional: true);
        var exact = new double[xyz.Length];
        var approximate = new double[xyz.Length];

        RepulsionBruteForce.Accumulate(xyz, mass, 10, exact);
        var tree = new BarnesHutTree();
        tree.Build(xyz, mass);
        tree.AccumulateRepulsion(xyz, mass, 10, 0.8, approximate);

        var errorSum = 0.0;
        var exactSum = 0.0;
        for (var i = 0; i < exact.Length; i++)
        {
            errorSum += Math.Abs(exact[i] - approximate[i]);
            exactSum += Math.Abs(exact[i]);
        }

        Assert.True(errorSum <= 0.05 * exactSum, $"Mean relative error {errorSum / exactSum:P2} exceeds 5%.");
    }

    [Fact]
    public void Coincident_bodies_do_not_produce_non_finite_forces()
    {
        double[] xyz = [1, 1, 0, 1, 1, 0, 1, 1, 0, 5, 5, 0];
        double[] mass = [1, 1, 1, 1];
        var forces = new double[xyz.Length];

        var tree = new BarnesHutTree();
        tree.Build(xyz, mass);
        tree.AccumulateRepulsion(xyz, mass, 10, 0.5, forces);

        Assert.All(forces, value => Assert.True(double.IsFinite(value)));
    }
}
