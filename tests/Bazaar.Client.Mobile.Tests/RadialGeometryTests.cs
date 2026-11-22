using Bazaar.Client.Mobile;

namespace Bazaar.Client.Mobile.Tests;

public class RadialGeometryTests
{
    private static readonly GesturePoint _origin = new(200, 300);
    private static readonly string[] _four = ["Up", "Right", "Down", "Left"];

    [Fact]
    public void Inside_the_centre_disc_is_the_centre_and_far_outside_is_empty()
    {
        var g = new RadialGeometry(_four, centreRadiusDp: 40, outerRadiusDp: 150);

        Assert.Equal(RadialSlotKind.Centre, g.SlotAt(_origin, new(210, 310)).Kind);
        Assert.Equal(RadialSlotKind.Empty, g.SlotAt(_origin, new(200, 100)).Kind);
    }

    [Theory]
    [InlineData(200, 220, "Up")]
    [InlineData(280, 300, "Right")]
    [InlineData(200, 380, "Down")]
    [InlineData(120, 300, "Left")]
    public void Sectors_run_clockwise_from_straight_up(double x, double y, string expected)
    {
        var g = new RadialGeometry(_four, centreRadiusDp: 40, outerRadiusDp: 150);

        var slot = g.SlotAt(_origin, new(x, y));

        Assert.Equal(RadialSlotKind.Sector, slot.Kind);
        Assert.Equal(expected, slot.SectorId);
    }

    [Fact]
    public void Seven_sectors_each_get_a_share_of_the_ring_and_wrap_at_the_top()
    {
        var ids = AndroidSettings.DefaultRadialSectors;
        var g = new RadialGeometry(ids);

        var seen = new HashSet<string>();
        for (var i = 0; i < ids.Count; i++)
        {
            var c = g.SectorCentre(i);
            var slot = g.SlotAt(_origin, new(_origin.X + c.X, _origin.Y + c.Y));
            Assert.Equal(ids[i], slot.SectorId);
            seen.Add(slot.SectorId!);
        }

        Assert.Equal(ids.Count, seen.Count);
        // Just left of straight up still belongs to the first sector.
        Assert.Equal(ids[0], g.SlotAt(_origin, new(190, 170)).SectorId);
    }

    [Fact]
    public void Bad_geometry_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new RadialGeometry([]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RadialGeometry(_four, 100, 50));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RadialGeometry(_four, 0, 50));
    }

    [Fact]
    public void The_geometry_feeds_the_radial_state_machine_end_to_end()
    {
        var g = new RadialGeometry(_four, 40, 150);
        var menu = new RadialMenu(_four);
        menu.Press();

        menu.MoveTo(g.SlotAt(_origin, new(280, 300)));
        var outcome = menu.Release();

        Assert.Equal(RadialOutcome.OpenSector, outcome);
    }
}
