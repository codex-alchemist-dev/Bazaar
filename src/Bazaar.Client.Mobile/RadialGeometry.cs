namespace Bazaar.Client.Mobile;

/// <summary>
/// Maps a finger position to a <see cref="RadialSlot"/> (doc 29, Radial and dictation). The centre is a disc, the sectors
/// share the ring around it, the first sector is centred straight up and the rest follow clockwise, and anything beyond
/// the outer radius is empty, so releasing there cancels.
/// </summary>
public sealed class RadialGeometry
{
    private readonly IReadOnlyList<string> _sectorIds;

    public RadialGeometry(IReadOnlyList<string> sectorIds, double centreRadiusDp = 48, double outerRadiusDp = 160)
    {
        ArgumentNullException.ThrowIfNull(sectorIds);
        if (sectorIds.Count == 0)
        {
            throw new ArgumentException("A radial menu needs at least one sector.", nameof(sectorIds));
        }

        if (centreRadiusDp <= 0 || outerRadiusDp <= centreRadiusDp)
        {
            throw new ArgumentOutOfRangeException(nameof(outerRadiusDp), "The outer radius must be larger than the centre radius, which must be positive.");
        }

        _sectorIds = sectorIds;
        CentreRadiusDp = centreRadiusDp;
        OuterRadiusDp = outerRadiusDp;
    }

    public double CentreRadiusDp { get; }

    public double OuterRadiusDp { get; }

    /// <summary>The slot under <paramref name="point"/> when the menu opened at <paramref name="origin"/>.</summary>
    public RadialSlot SlotAt(GesturePoint origin, GesturePoint point)
    {
        var dx = point.X - origin.X;
        var dy = point.Y - origin.Y;
        var distance = Math.Sqrt((dx * dx) + (dy * dy));
        if (distance <= CentreRadiusDp)
        {
            return RadialSlot.Centre;
        }

        if (distance > OuterRadiusDp)
        {
            return RadialSlot.Empty;
        }

        return RadialSlot.ForSector(_sectorIds[IndexOf(dx, dy)]);
    }

    /// <summary>The centre of a sector, as an offset from the menu origin, for drawing its label.</summary>
    public GesturePoint SectorCentre(int index)
    {
        var step = 2 * Math.PI / _sectorIds.Count;
        var angle = index * step;
        var radius = (CentreRadiusDp + OuterRadiusDp) / 2;
        return new GesturePoint(radius * Math.Sin(angle), -radius * Math.Cos(angle));
    }

    private int IndexOf(double dx, double dy)
    {
        var step = 2 * Math.PI / _sectorIds.Count;

        // Clockwise angle from straight up, shifted half a step so sector 0 is centred on the vertical.
        var angle = Math.Atan2(dx, -dy);
        if (angle < 0)
        {
            angle += 2 * Math.PI;
        }

        angle = (angle + (step / 2)) % (2 * Math.PI);
        return Math.Min((int)(angle / step), _sectorIds.Count - 1);
    }
}
