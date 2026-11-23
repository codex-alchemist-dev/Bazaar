using Bazaar.Server.Constellation.Layout;
using Bazaar.Server.Settings;

namespace Bazaar.Server.Tests.Constellation.Layout;

public class SolarOrbitLayoutServiceTests
{
    [Fact]
    public void Default_options_match_SolarOrbitLayout_defaults()
    {
        var graph = Graph(6, (0, 1), (0, 2), (1, 3), (2, 4), (4, 5));

        var fromService = _create().Layout(graph, 0);
        var direct = SolarOrbitLayout.Layout(graph, 0);

        Assert.Equal(direct, fromService);
    }

    [Fact]
    public void Null_options_match_default_options()
    {
        var graph = Graph(4, (0, 1), (1, 2), (0, 3));

        Assert.Equal(_create().Layout(graph, 0, new OrbitLayoutOptions()), _create().Layout(graph, 0, null));
    }

    [Theory]
    [InlineData(OrbitRadiusMode.Age, ConstellationOrbitRadius.Age)]
    [InlineData(OrbitRadiusMode.Equal, ConstellationOrbitRadius.Equal)]
    public void Options_map_to_SolarOrbitLayout_for_every_field(OrbitRadiusMode mode, ConstellationOrbitRadius settingMode)
    {
        var graph = Graph(7, (0, 1), (0, 2), (1, 3), (2, 4), (4, 5), (5, 6));
        var contract = new OrbitLayoutOptions
        {
            Seed = 42,
            OrbitSpacing = 7,
            OrbitRadiusMode = mode,
            MinOrbitRadius = 12,
            MaxOrbitRadius = 80,
        };
        var serverOptions = new LayoutOptions
        {
            Seed = 42,
            OrbitSpacing = 7,
            OrbitRadiusMode = settingMode,
            MinOrbitRadius = 12,
            MaxOrbitRadius = 80,
        };
        double[] ages = [0, 3, 1, 8, 2, 5, 13];

        Assert.Equal(SolarOrbitLayout.Layout(graph, 0, serverOptions), _create().Layout(graph, 0, contract));
        Assert.Equal(SolarOrbitLayout.Layout(graph, 0, serverOptions, ages), _create().Layout(graph, 0, contract, ages));
    }

    [Fact]
    public void Activity_ages_are_passed_through()
    {
        var graph = Graph(4, (0, 1), (0, 2), (0, 3));
        double[] ages = [0, 1, 2, 3];

        var positions = _create().Layout(graph, 0, new OrbitLayoutOptions { MinOrbitRadius = 10, MaxOrbitRadius = 50 }, ages);

        // Youngest (age 1) sits at the min radius, oldest (age 3) at the max radius.
        Assert.Equal(10, Radius(positions, 1), 3);
        Assert.Equal(50, Radius(positions, 3), 3);
    }

    [Fact]
    public void Mismatched_activity_ages_are_rejected_like_SolarOrbitLayout()
    {
        var graph = Graph(3, (0, 1), (0, 2));

        Assert.Throws<ArgumentException>(() => _create().Layout(graph, 0, null, [1, 2]));
    }

    [Fact]
    public void Empty_graph_returns_empty_positions()
    {
        var graph = Graph(0);

        Assert.Empty(_create().Layout(graph, 0));
    }

    [Fact]
    public void Null_graph_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => _create().Layout(null!, 0));
    }

    [Fact]
    public void Operator_mode_enum_values_match_the_server_setting()
    {
        Assert.Equal(Enum.GetNames<ConstellationOrbitRadius>(), Enum.GetNames<OrbitRadiusMode>());

        foreach (var name in Enum.GetNames<ConstellationOrbitRadius>())
        {
            var setting = Enum.Parse<ConstellationOrbitRadius>(name);
            var contract = Enum.Parse<OrbitRadiusMode>(name);

            Assert.Equal((int)setting, (int)contract);
        }
    }

    /// <summary>Creates the adapter behind the contract type only, so the tests exercise the interface a server module would use.</summary>
    private static readonly Func<IConstellationOrbitLayout> _create = () => new SolarOrbitLayoutService();

    private static LayoutGraph Graph(int nodeCount, params (int From, int To)[] edges)
    {
        var ids = Enumerable.Range(0, nodeCount).Select(i => $"n{i}").ToArray();
        var layoutEdges = edges.Select(e => new LayoutEdge(e.From, e.To)).ToArray();
        return new LayoutGraph(ids, layoutEdges);
    }

    private static double Radius(float[] positions, int node)
    {
        var x = positions[node * 2];
        var y = positions[node * 2 + 1];
        return Math.Sqrt((x * x) + (y * y));
    }
}
