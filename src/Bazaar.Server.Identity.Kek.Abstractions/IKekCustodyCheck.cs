namespace Bazaar.Server.Identity.Kek;

/// <summary>
/// What the custody check found. <see cref="Stored"/> is how many live wrapped keys it tried, <see cref="Opened"/> how many of
/// them the host KEK opened. The check stops at the first key that opens, so <see cref="Opened"/> is 0 or 1.
/// </summary>
public sealed record KekCustodyResult(int Stored, int Opened)
{
    /// <summary>True when the database holds no wrapped key yet (a first start) or the host KEK opens one of them.</summary>
    public bool Matches => Stored == 0 || Opened > 0;
}

/// <summary>
/// Checks at start that the host KEK is the one this database's keys were wrapped under (doc 14, envelope encryption; doc 03
/// startup). A host started against the wrong or an unmounted data root opens, or mints, a different KEK; without this check it
/// runs, every send and every read of an encrypted field fails, and readiness stays green.
/// </summary>
public interface IKekCustodyCheck
{
    /// <summary>Tries the stored wrapped keys under the host KEK. Changes nothing.</summary>
    Task<KekCustodyResult> CheckAsync(CancellationToken cancellationToken);
}
