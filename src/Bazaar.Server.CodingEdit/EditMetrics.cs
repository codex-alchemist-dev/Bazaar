namespace Bazaar.Server.CodingEdit;

/// <summary>A point-in-time copy of the in-process edit counters.</summary>
/// <param name="Edits">Edits attempted.</param>
/// <param name="Applied">Edits that applied, including no-ops.</param>
/// <param name="RefusedByReason">Refusals counted by reason. Reasons with no refusals are absent.</param>
public sealed record EditMetricsSnapshot(long Edits, long Applied, IReadOnlyDictionary<EditRejectReason, long> RefusedByReason)
{
    /// <summary>Refusals of every reason.</summary>
    public long Refused => RefusedByReason.Values.Sum();

    /// <summary>Malformed refusals over edits attempted (doc 17, "Metrics: malformed-edit rate"). Zero when there were no edits.</summary>
    public double MalformedRate => Edits == 0 ? 0 : (double)CountOf(EditRejectReason.Malformed) / Edits;

    /// <summary>The count for one refusal reason.</summary>
    public long CountOf(EditRejectReason reason) => RefusedByReason.TryGetValue(reason, out var count) ? count : 0;
}

/// <summary>
/// In-process edit counters for the malformed-edit rate (doc 17, "Edit formats and benchmark"). The benchmark runner reads them
/// through <see cref="Snapshot"/>. They are not persisted.
/// </summary>
public sealed class EditMetrics
{
    private readonly Lock _gate = new();
    private readonly Dictionary<EditRejectReason, long> _refused = [];
    private long _edits;
    private long _applied;

    /// <summary>Counts one edit outcome.</summary>
    public void Record(EditResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        lock (_gate)
        {
            _edits++;
            if (result.Applied)
            {
                _applied++;
                return;
            }

            var reason = result.Reason ?? EditRejectReason.Malformed;
            _refused[reason] = _refused.GetValueOrDefault(reason) + 1;
        }
    }

    /// <summary>Copies the counters.</summary>
    public EditMetricsSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new EditMetricsSnapshot(_edits, _applied, new Dictionary<EditRejectReason, long>(_refused));
        }
    }
}
