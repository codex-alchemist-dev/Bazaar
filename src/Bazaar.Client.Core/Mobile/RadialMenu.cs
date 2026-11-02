namespace Bazaar.Client.Core.Mobile;

/// <summary>Where the pointer is inside the radial menu.</summary>
public readonly record struct RadialSlot
{
    private RadialSlot(RadialSlotKind kind, string? sectorId)
    {
        Kind = kind;
        SectorId = sectorId;
    }

    public RadialSlotKind Kind { get; }

    /// <summary>Set only when <see cref="Kind"/> is <see cref="RadialSlotKind.Sector"/>.</summary>
    public string? SectorId { get; }

    /// <summary>The centre, where dictation runs.</summary>
    public static RadialSlot Centre { get; } = new(RadialSlotKind.Centre, null);

    /// <summary>Outside every sector. Releasing here cancels.</summary>
    public static RadialSlot Empty { get; } = new(RadialSlotKind.Empty, null);

    public static RadialSlot ForSector(string sectorId) => new(RadialSlotKind.Sector, sectorId);
}

public enum RadialSlotKind
{
    Centre,
    Sector,
    Empty,
}

public enum RadialPhase
{
    Idle,
    Open,
}

public enum RadialOutcome
{
    /// <summary>Released with nothing to do (the gesture was cancelled or released on an empty area).</summary>
    Cancelled,

    /// <summary>Released in the centre while dictation was live: the transcript goes to the composer for review.</summary>
    PlaceTranscript,

    /// <summary>Released in a sector: that action opens and dictation is discarded.</summary>
    OpenSector,
}

/// <summary>
/// Press-and-hold radial state machine (doc 29, Radial and dictation). No UI: the head feeds it pointer
/// events and starts or stops the recorder when <see cref="Dictating"/> changes.
/// </summary>
public sealed class RadialMenu
{
    private readonly HashSet<string> _sectors;
    private bool _dictationCancelled;

    public RadialMenu(IEnumerable<string> sectorIds)
    {
        ArgumentNullException.ThrowIfNull(sectorIds);
        var ids = sectorIds.ToList();
        if (ids.Count == 0)
        {
            throw new ArgumentException("A radial menu needs at least one sector.", nameof(sectorIds));
        }

        _sectors = new HashSet<string>(ids, StringComparer.Ordinal);
        if (_sectors.Count != ids.Count)
        {
            throw new ArgumentException("Sector ids must be unique.", nameof(sectorIds));
        }

        SectorIds = ids;
    }

    public IReadOnlyList<string> SectorIds { get; }

    public RadialPhase Phase { get; private set; } = RadialPhase.Idle;

    public RadialSlot Slot { get; private set; } = RadialSlot.Centre;

    /// <summary>True while the recorder should run: the menu is open, the pointer is in the centre and no sector was entered.</summary>
    public bool Dictating => Phase == RadialPhase.Open && Slot.Kind == RadialSlotKind.Centre && !_dictationCancelled;

    /// <summary>Press and hold anywhere: the menu opens with dictation already live at the centre.</summary>
    public void Press()
    {
        if (Phase != RadialPhase.Idle)
        {
            return;
        }

        Phase = RadialPhase.Open;
        Slot = RadialSlot.Centre;
        _dictationCancelled = false;
    }

    /// <summary>Pointer moved. Entering a sector cancels dictation for the rest of this gesture.</summary>
    public void MoveTo(RadialSlot slot)
    {
        if (Phase != RadialPhase.Open)
        {
            return;
        }

        if (slot.Kind == RadialSlotKind.Sector && !_sectors.Contains(slot.SectorId!))
        {
            throw new ArgumentException($"Unknown radial sector '{slot.SectorId}'.", nameof(slot));
        }

        Slot = slot;
        if (slot.Kind == RadialSlotKind.Sector)
        {
            _dictationCancelled = true;
        }
    }

    /// <summary>Finger lifted. Returns what the head should do and closes the menu.</summary>
    public RadialOutcome Release()
    {
        if (Phase != RadialPhase.Open)
        {
            return RadialOutcome.Cancelled;
        }

        var outcome = Slot.Kind switch
        {
            RadialSlotKind.Centre when !_dictationCancelled => RadialOutcome.PlaceTranscript,
            RadialSlotKind.Sector => RadialOutcome.OpenSector,
            _ => RadialOutcome.Cancelled,
        };

        Close();
        return outcome;
    }

    /// <summary>Gesture aborted (for example the system took the touch). Nothing is placed or opened.</summary>
    public void Cancel()
    {
        if (Phase == RadialPhase.Open)
        {
            Close();
        }
    }

    private void Close()
    {
        Phase = RadialPhase.Idle;
        Slot = RadialSlot.Centre;
        _dictationCancelled = false;
    }
}
