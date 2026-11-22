using Bazaar.Client.Hive.Grants;

namespace Bazaar.Client.Hive.Tests;

public class HiveGrantMatrixTests
{
    private static readonly HiveGrant[] _grants =
    [
        new("beta", "alpha", "procedural", "owner"),
        new("alpha", "beta", "procedural", "owner"),
        new("alpha", "gamma", "failure", "owner"),
    ];

    [Fact]
    public void Projects_are_the_sorted_union_of_both_axes_and_extra_origins()
    {
        var matrix = HiveGrantMatrix.Build(_grants, ["delta"]);

        Assert.Equal(["alpha", "beta", "delta", "gamma"], matrix.Projects);
    }

    [Fact]
    public void One_grid_per_kind_with_granted_cells_and_no_self_grant()
    {
        var matrix = HiveGrantMatrix.Build(_grants);

        Assert.Equal(["failure", "procedural"], matrix.Kinds.Select(kind => kind.Kind));
        var procedural = matrix.Kinds[1];
        var alphaRow = procedural.Rows.Single(row => row.FromProject == "alpha");
        Assert.Equal(["alpha", "beta", "gamma"], alphaRow.Cells.Select(cell => cell.Key.ToProject));
        Assert.Equal([false, true, false], alphaRow.Cells.Select(cell => cell.Granted));
        Assert.Equal([true, false, false], alphaRow.Cells.Select(cell => cell.IsSelf));
        Assert.False(alphaRow.Cells[0].Granted);
    }

    [Fact]
    public void Cells_carry_the_grant_key_so_a_toggle_can_request_it()
    {
        var matrix = HiveGrantMatrix.Build(_grants);

        var cell = matrix.Kinds[0].Rows.Single(row => row.FromProject == "alpha").Cells.Single(cell => cell.Key.ToProject == "gamma");
        Assert.Equal(new HiveGrantKey("alpha", "gamma", "failure"), cell.Key);
        Assert.True(cell.Granted);
    }

    [Fact]
    public void Empty_grants_give_an_empty_matrix()
    {
        var matrix = HiveGrantMatrix.Build([]);

        Assert.Empty(matrix.Projects);
        Assert.Empty(matrix.Kinds);
    }

    [Fact]
    public void Build_is_independent_of_grant_order()
    {
        var forward = Flatten(HiveGrantMatrix.Build(_grants));
        var reversed = Flatten(HiveGrantMatrix.Build(_grants.Reverse()));

        Assert.Equal(forward, reversed);
    }

    /// <summary>Every cell as text, so two matrices compare by content rather than by array reference.</summary>
    private static List<string> Flatten(HiveGrantMatrix matrix) =>
        matrix.Projects.Select(project => "project " + project)
            .Concat(matrix.Kinds.SelectMany(kind => kind.Rows.SelectMany(row => row.Cells.Select(cell =>
                $"{kind.Kind} {cell.Key.FromProject}>{cell.Key.ToProject} {cell.Granted} {cell.IsSelf}"))))
            .ToList();

    [Fact]
    public void Grant_key_comes_from_the_grant_fields()
    {
        Assert.Equal(new HiveGrantKey("alpha", "beta", "procedural"), new HiveGrant("alpha", "beta", "procedural", "owner").Key);
    }
}
