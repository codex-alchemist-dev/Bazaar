namespace Bazaar.Client.LocalInference.Scheduling;

/// <summary>Platform power state. Background summaries pause while <see cref="IsBatterySaver"/> is true (doc 31, Behaviour: Power).</summary>
public interface IPowerState
{
    bool IsBatterySaver { get; }
}

/// <summary>Default power state: never in battery saver, so background work is never paused.</summary>
public sealed class UnrestrictedPowerState : IPowerState
{
    public static UnrestrictedPowerState Instance { get; } = new();

    public bool IsBatterySaver => false;
}
