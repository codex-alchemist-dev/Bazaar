namespace Bazaar.Kodo.Responder;

/// <summary>
/// The only two recovery actions an operator may allow to run unattended (doc 46 sections 14.5, 16.5 and 20; owner decision). Isolate and
/// roll back are deliberately not members: they always need step-up approval, so no setting can name them.
/// </summary>
public enum UnattendedRecovery
{
    /// <summary>The shipped host restart through the sentinel (<c>fix.host_restart</c>).</summary>
    HostRestart,

    /// <summary>The shipped switch to the other verified slot (<c>fix.slot_switch</c>).</summary>
    SlotSwitch,
}

/// <summary>
/// Parsing and validation of the <c>kodo.recover.unattended</c> list. Matching is exact and ordinal: no trimming, no case folding, and no
/// aliases, so a near miss is refused instead of guessed at. A refused value never widens what runs unattended.
/// </summary>
public static class UnattendedRecoveries
{
    /// <summary>The setting value for <see cref="UnattendedRecovery.HostRestart"/>.</summary>
    public const string HostRestartName = "host_restart";

    /// <summary>The setting value for <see cref="UnattendedRecovery.SlotSwitch"/>.</summary>
    public const string SlotSwitchName = "slot_switch";

    /// <summary>The setting value for one action.</summary>
    public static string NameOf(UnattendedRecovery recovery) => recovery switch
    {
        UnattendedRecovery.HostRestart => HostRestartName,
        UnattendedRecovery.SlotSwitch => SlotSwitchName,
        _ => throw new ArgumentOutOfRangeException(nameof(recovery), recovery, null),
    };

    /// <summary>Reads one setting value. Only the two exact names match; everything else, including null, is false.</summary>
    public static bool TryParse(string? value, out UnattendedRecovery recovery)
    {
        switch (value)
        {
            case HostRestartName:
                recovery = UnattendedRecovery.HostRestart;
                return true;
            case SlotSwitchName:
                recovery = UnattendedRecovery.SlotSwitch;
                return true;
            default:
                recovery = default;
                return false;
        }
    }

    /// <summary>The first problem with a setting list, or null when every value is one of the two names. Repeats are harmless and collapse.</summary>
    public static string? Problem(IReadOnlyList<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        foreach (string? value in values)
        {
            if (!TryParse(value, out _))
            {
                return "unattended_recovery_not_allowed";
            }
        }

        return null;
    }

    /// <summary>The allowed set for a setting list. Throws when any value is refused, so a bad list never yields a partial set.</summary>
    public static IReadOnlySet<UnattendedRecovery> Parse(IReadOnlyList<string> values)
    {
        if (Problem(values) is { } problem)
        {
            throw new ArgumentException(problem, nameof(values));
        }

        HashSet<UnattendedRecovery> set = [];
        foreach (string value in values)
        {
            if (TryParse(value, out UnattendedRecovery recovery))
            {
                set.Add(recovery);
            }
        }

        return set;
    }
}
