namespace Bazaar.Client.Core.Tour;

/// <summary>Where a tour stands. A finished tour can be re-run with <see cref="TourSession.Restart"/>.</summary>
public enum TourStatus
{
    NotStarted,
    Active,
    Completed,
    Skipped,
}

/// <summary>The part of a tour that is saved between runs: which step and how it ended.</summary>
public sealed record TourProgress(string TourId, string? StepId, TourStatus Status);

/// <summary>
/// Walks a tour: next, back, skip, finish and re-run. Skipping is always allowed, from any step (doc 41 onboarding:
/// skippable and re-runnable). Pure: the head draws the current step and saves <see cref="Progress"/>.
/// </summary>
public sealed class TourSession
{
    private readonly TourDefinition _definition;
    private int _index = -1;
    private TourStatus _status = TourStatus.NotStarted;

    private TourSession(TourDefinition definition)
    {
        _definition = definition;
    }

    /// <summary>Starts a session at the first step of a tour nobody has run yet.</summary>
    public static TourSession Start(TourDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var session = new TourSession(definition);
        session.Begin();
        return session;
    }

    /// <summary>
    /// Resumes from saved progress. A saved step that no longer exists goes back to the first step. A tour
    /// that already ended stays ended, so the head shows the re-run choice instead of the tour.
    /// </summary>
    public static TourSession Resume(TourDefinition definition, TourProgress? saved)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var session = new TourSession(definition);
        if (saved is null || saved.TourId != definition.Id)
        {
            session.Begin();
            return session;
        }

        if (saved.Status is TourStatus.Completed or TourStatus.Skipped)
        {
            session._status = saved.Status;
            return session;
        }

        session._status = TourStatus.Active;
        int index = saved.StepId is null ? -1 : definition.Steps.ToList().FindIndex(s => s.Id == saved.StepId);
        session._index = index < 0 ? 0 : index;
        return session;
    }

    public TourStatus Status => _status;

    public bool IsActive => _status == TourStatus.Active;

    /// <summary>The step to show, or null when the tour is not active.</summary>
    public TourStep? Current => IsActive ? _definition.Steps[_index] : null;

    /// <summary>Zero-based index of the current step, or -1 when not active.</summary>
    public int Index => IsActive ? _index : -1;

    public int StepCount => _definition.Steps.Count;

    /// <summary>What to save. Taken after every change.</summary>
    public TourProgress Progress => new(_definition.Id, Current?.Id, _status);

    /// <summary>Moves to the next step. On the last step it completes the tour.</summary>
    public void Next()
    {
        if (!IsActive)
        {
            return;
        }

        if (_index + 1 >= _definition.Steps.Count)
        {
            _status = TourStatus.Completed;
            _index = -1;
            return;
        }

        _index++;
    }

    /// <summary>Moves to the previous step. The first step stays put.</summary>
    public void Back()
    {
        if (IsActive && _index > 0)
        {
            _index--;
        }
    }

    /// <summary>Ends the tour early. Allowed from any step.</summary>
    public void Skip()
    {
        if (IsActive)
        {
            _status = TourStatus.Skipped;
            _index = -1;
        }
    }

    /// <summary>Starts the tour again from the first step, whatever its previous status.</summary>
    public void Restart() => Begin();

    private void Begin()
    {
        _status = TourStatus.Active;
        _index = 0;
    }
}

/// <summary>
/// Stores tour progress for this viewer. This is a per-device convenience, so an unavailable store must never block a tour.
/// Load returns null when nothing is saved. Save may fail; callers ignore that failure.
/// </summary>
public interface ITourStateStore
{
    TourProgress? Load(string tourId);

    void Save(TourProgress progress);
}

/// <summary>In-memory store for tests and for heads that do not persist tour progress.</summary>
public sealed class InMemoryTourStateStore : ITourStateStore
{
    private readonly Dictionary<string, TourProgress> _saved = new(StringComparer.Ordinal);

    public TourProgress? Load(string tourId) => _saved.TryGetValue(tourId, out TourProgress? progress) ? progress : null;

    public void Save(TourProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        _saved[progress.TourId] = progress;
    }
}
