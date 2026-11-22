namespace Bazaar.Kodo.Standby;

/// <summary>What a node says about a presented fencing token.</summary>
public enum FencingVerdict
{
    /// <summary>The token is the current epoch of a node that is primary. The only verdict that admits a write.</summary>
    Current,

    /// <summary>The token is older than the current epoch (or not a real epoch). Always rejected.</summary>
    Stale,

    /// <summary>The token is newer than this node's epoch, so this node is the stale one. Rejected, and the node fences itself.</summary>
    Ahead,

    /// <summary>This node is not primary, so it admits nothing.</summary>
    NotPrimary,
}

/// <summary>
/// The fencing token (doc 46 section 7.6): a number that only ever increases, one per lease holder change. It is written into every
/// journal record and every control action, so a stale primary is rejected. Epoch 0 means "no token" and is never admitted.
/// </summary>
public readonly record struct FencingToken : IComparable<FencingToken>
{
    /// <summary>Creates a token for <paramref name="epoch"/>, which must not be negative.</summary>
    public FencingToken(long epoch)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(epoch);
        Epoch = epoch;
    }

    /// <summary>The epoch number. 0 is "no token".</summary>
    public long Epoch { get; }

    /// <summary>The next token. Throws on overflow rather than wrapping.</summary>
    public FencingToken Next() => new(checked(Epoch + 1));

    /// <summary>Compares two tokens by epoch.</summary>
    public int CompareTo(FencingToken other) => Epoch.CompareTo(other.Epoch);

    /// <summary>
    /// Checks <paramref name="presented"/> against the <paramref name="current"/> epoch. A token below the current one is always
    /// stale, and a token with epoch 0 is stale whatever the current epoch is. This function does not look at the role.
    /// </summary>
    public static FencingVerdict Check(FencingToken presented, FencingToken current)
    {
        if (presented.Epoch <= 0 || presented.Epoch < current.Epoch)
        {
            return FencingVerdict.Stale;
        }

        return presented.Epoch == current.Epoch ? FencingVerdict.Current : FencingVerdict.Ahead;
    }

    /// <summary>True when <paramref name="left"/> has a lower epoch than <paramref name="right"/>.</summary>
    public static bool operator <(FencingToken left, FencingToken right) => left.CompareTo(right) < 0;

    /// <summary>True when <paramref name="left"/> has a lower or equal epoch.</summary>
    public static bool operator <=(FencingToken left, FencingToken right) => left.CompareTo(right) <= 0;

    /// <summary>True when <paramref name="left"/> has a higher epoch than <paramref name="right"/>.</summary>
    public static bool operator >(FencingToken left, FencingToken right) => left.CompareTo(right) > 0;

    /// <summary>True when <paramref name="left"/> has a higher or equal epoch.</summary>
    public static bool operator >=(FencingToken left, FencingToken right) => left.CompareTo(right) >= 0;

    /// <inheritdoc />
    public override string ToString() => Epoch.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
