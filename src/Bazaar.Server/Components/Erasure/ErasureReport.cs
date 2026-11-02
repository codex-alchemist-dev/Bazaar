namespace Bazaar.Server.Components.Erasure;

public enum PersonKeyOutcome
{
    /// <summary>This run destroyed the person's key or keys.</summary>
    Destroyed,

    /// <summary>The keys were already destroyed by an earlier run. A repeat run lands here and is not an error.</summary>
    AlreadyDestroyed,

    /// <summary>No key row exists for the person, so there is nothing to destroy. Treated as not destroyed (incomplete).</summary>
    NoKeyOnFile,

    /// <summary>Key destruction threw. The message is in <see cref="ErasureReport.KeyError"/>.</summary>
    Failed,
}

/// <summary>One participant's name and what it reported.</summary>
public sealed record ParticipantOutcome(string Name, ErasureParticipantResult Result);

/// <summary>
/// The outcome of one erasure run for one person. <see cref="IsComplete"/> is false whenever the key was not destroyed or any
/// participant failed; a failure never hides the key result, because both are recorded.
/// </summary>
public sealed record ErasureReport(
    Guid PersonId,
    PersonKeyOutcome KeyOutcome,
    string? KeyError,
    IReadOnlyList<ParticipantOutcome> Participants)
{
    /// <summary>True when the person's key is destroyed now or was destroyed by an earlier run.</summary>
    public bool KeyDestroyed => KeyOutcome is PersonKeyOutcome.Destroyed or PersonKeyOutcome.AlreadyDestroyed;

    /// <summary>True only when the key is destroyed and no participant failed. Mechanical result; not a legal finding.</summary>
    public bool IsComplete => KeyDestroyed && Participants.All(p => p.Result.Status != ErasureParticipantStatus.Failed);

    /// <summary>Human-readable reasons the erasure is incomplete. Empty when <see cref="IsComplete"/> is true.</summary>
    public IReadOnlyList<string> Failures
    {
        get
        {
            var reasons = new List<string>();
            switch (KeyOutcome)
            {
                case PersonKeyOutcome.Failed:
                    reasons.Add($"key destruction failed: {KeyError}");
                    break;
                case PersonKeyOutcome.NoKeyOnFile:
                    reasons.Add("no key on file for this person");
                    break;
            }

            foreach (var p in Participants.Where(p => p.Result.Status == ErasureParticipantStatus.Failed))
                reasons.Add($"participant '{p.Name}' failed: {p.Result.Error}");

            return reasons;
        }
    }
}
