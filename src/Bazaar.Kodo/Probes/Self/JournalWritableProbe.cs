namespace Bazaar.Kodo.Probes.Self;

/// <summary>
/// <c>kodo.self.journal_writable</c>. A refused test write to the journal is red, because Kodo cannot record its own
/// history. This id is not in the section 9.1 catalogue, so it needs a catalogue change before the probe is scheduled.
/// </summary>
public sealed class JournalWritableProbe
{
    public const string Id = "kodo.self.journal_writable";

    private readonly IKodoSelfReadings _readings;

    public JournalWritableProbe(IKodoSelfReadings readings)
    {
        _readings = readings ?? throw new ArgumentNullException(nameof(readings));
    }

    /// <summary>Attempts the write and returns the observation. A failed attempt is unknown, never green.</summary>
    public async Task<ProbeResult> RunAsync(CancellationToken cancellationToken)
    {
        bool writable;
        try
        {
            writable = await _readings.CanWriteJournalAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return ProbeResult.Unknown(ProbeCauses.Unreadable);
        }

        return writable ? ProbeResult.Green() : ProbeResult.Red(ProbeCauses.JournalNotWritable);
    }
}
