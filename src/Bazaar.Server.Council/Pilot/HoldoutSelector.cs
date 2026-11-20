using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Bazaar.Server.Council.Pilot;

/// <summary>
/// Deterministic holdout arm for the pilot (doc 10, council.pilot_holdout_share). A vote in the holdout skips follow-up.
/// The same vote id and seed always land in the same arm.
/// </summary>
public static class HoldoutSelector
{
    private const double _twoTo64 = 18446744073709551616.0;

    /// <summary>
    /// SHA-256 of the vote id and seed; the first 8 bytes (big-endian) are a uniform number in [0, 2^64) that is compared
    /// with <paramref name="share"/>. Shares 0 and 1 are exact: none and all.
    /// </summary>
    /// <param name="voteId">The vote id.</param>
    /// <param name="share">Holdout share, between 0 and 1 inclusive.</param>
    /// <param name="seed">Pilot seed, so a new pilot can use a new split.</param>
    /// <exception cref="ArgumentOutOfRangeException">The share is outside 0 to 1.</exception>
    public static bool IsHoldout(string voteId, double share, string seed)
    {
        ArgumentNullException.ThrowIfNull(voteId);
        ArgumentNullException.ThrowIfNull(seed);
        if (double.IsNaN(share) || share < 0 || share > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(share), share, "Holdout share must be between 0 and 1.");
        }

        if (share == 0)
        {
            return false;
        }

        if (share == 1)
        {
            return true;
        }

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(voteId + "\u001f" + seed));
        var bucket = BinaryPrimitives.ReadUInt64BigEndian(digest.AsSpan(0, 8));
        return bucket / _twoTo64 < share;
    }
}
