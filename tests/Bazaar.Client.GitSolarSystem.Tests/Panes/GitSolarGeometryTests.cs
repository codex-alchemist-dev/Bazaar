using Bazaar.Client.GitSolarSystem.Panes;
using Bazaar.Constellation.Rendering.Core;

namespace Bazaar.Client.GitSolarSystem.Tests.Panes;

public class GitSolarGeometryTests
{
    [Fact]
    public void The_core_centre_is_the_mean_of_the_default_branch_only()
    {
        float[] xy = [0f, 0f, 4f, 0f, 100f, 100f];
        int[] branches = [0, 0, 1];

        var (x, y) = GitSolarGeometry.CoreCentre(xy, branches);

        Assert.Equal(2f, x);
        Assert.Equal(0f, y);
    }

    [Fact]
    public void The_core_centre_is_the_origin_when_the_default_branch_has_no_commits()
    {
        float[] xy = [5f, 5f];
        int[] branches = [1];

        Assert.Equal((0f, 0f), GitSolarGeometry.CoreCentre(xy, branches));
    }

    [Fact]
    public void A_ring_radius_is_the_mean_distance_of_that_branch_from_the_core()
    {
        // Branch 1 sits at distance 3 and 4 from the origin: the ring is their mean.
        float[] xy = [3f, 0f, 0f, 4f, 50f, 0f];
        int[] branches = [1, 1, 0];

        Assert.Equal(3.5f, GitSolarGeometry.RingRadius(xy, branches, 1, 0f, 0f), 4);
    }

    [Fact]
    public void A_branch_without_commits_has_no_ring()
    {
        float[] xy = [1f, 1f];
        int[] branches = [0];

        Assert.Equal(0f, GitSolarGeometry.RingRadius(xy, branches, 2, 0f, 0f));
    }

    [Fact]
    public void The_nearest_node_is_the_closest_one_inside_the_radius()
    {
        float[] xy = [0f, 0f, 10f, 0f, 3f, 4f];

        Assert.Equal(2, GitSolarGeometry.NearestNode(xy, 4f, 4f, 3f));
        Assert.Equal(1, GitSolarGeometry.NearestNode(xy, 9f, 0f, 1.5f));
    }

    [Fact]
    public void A_click_outside_every_radius_finds_no_node()
    {
        float[] xy = [0f, 0f, 10f, 0f];

        Assert.Equal(-1, GitSolarGeometry.NearestNode(xy, 5f, 5f, 2f));
        Assert.Equal(-1, GitSolarGeometry.NearestNode(xy, float.NaN, 0f, 8f));
        Assert.Equal(-1, GitSolarGeometry.NearestNode(xy, 0f, 0f, -1f));
    }

    [Fact]
    public void The_radius_boundary_is_included_and_ties_go_to_the_lower_index()
    {
        float[] xy = [0f, 0f, 2f, 0f];

        // Both nodes are exactly 1 away from (1, 0): the lower index wins.
        Assert.Equal(0, GitSolarGeometry.NearestNode(xy, 1f, 0f, 1f));
    }

    [Fact]
    public void Nodes_with_non_finite_positions_never_match()
    {
        float[] xy = [float.NaN, 0f, 1f, 0f];

        Assert.Equal(1, GitSolarGeometry.NearestNode(xy, 0f, 0f, 5f));
    }

    [Fact]
    public void The_fit_centres_on_the_bounds_and_uses_the_tighter_axis()
    {
        // Bounds are 10 wide and 20 tall: in a 100 by 100 viewport the height limits the zoom to 5.
        float[] xy = [0f, 0f, 10f, 20f];

        var fit = GitSolarGeometry.FitView(xy, 100, 100, 0f);

        Assert.Equal(5f, fit.CenterX);
        Assert.Equal(10f, fit.CenterY);
        Assert.Equal(5f, fit.Zoom);
    }

    [Fact]
    public void The_fit_keeps_the_padding_free_on_each_side()
    {
        float[] xy = [0f, 0f, 10f, 0f];

        // 100 px wide, 20 px padding each side: 60 px left for 10 units, so zoom 6.
        var fit = GitSolarGeometry.FitView(xy, 100, 100, 20f);

        Assert.Equal(6f, fit.Zoom);
    }

    [Fact]
    public void A_single_node_or_no_nodes_fit_at_zoom_one()
    {
        Assert.Equal(new GitSolarViewFit(7f, 8f, 1f), GitSolarGeometry.FitView([7f, 8f], 100, 100, 0f));
        Assert.Equal(new GitSolarViewFit(0f, 0f, 1f), GitSolarGeometry.FitView([], 100, 100, 0f));
    }

    [Fact]
    public void The_fit_zoom_is_clamped_to_the_camera_range()
    {
        float[] xy = [0f, 0f, 0.0001f, 0f];

        var fit = GitSolarGeometry.FitView(xy, 100, 100, 0f);

        Assert.Equal(Camera2D.MaxZoom, fit.Zoom);
    }

    [Fact]
    public void Applying_a_fit_sets_the_camera_zoom_and_centre()
    {
        var camera = new Camera2D();

        GitSolarGeometry.ApplyFit(camera, new GitSolarViewFit(5f, 10f, 5f), 100, 100);

        Assert.Equal(5f, camera.Zoom);
        Assert.Equal(5f, camera.CenterX);
        Assert.Equal(10f, camera.CenterY);
    }
}
