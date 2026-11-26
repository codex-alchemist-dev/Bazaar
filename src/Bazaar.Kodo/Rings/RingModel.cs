using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Bazaar.Kodo.Rings;

/// <summary>The staged rings of a release (doc 46 section 16.6), in rollout order.</summary>
public enum ReleaseRing
{
    /// <summary>A small set of devices that gets the release first.</summary>
    Canary = 0,

    /// <summary>The next, larger slice.</summary>
    Early = 1,

    /// <summary>Everyone else.</summary>
    Broad = 2,
}

/// <summary>The release channels. Doc 49 owns the channel policy; Kodo only needs the name to pick sensitivity.</summary>
public enum ReleaseChannel
{
    Stable,
    Beta,
    Nightly,
    Lts,
}

/// <summary>
/// Ring sizes (doc 46 section 16.6). Sizes are shares of the fleet in basis points (100 is 1 percent), exclusive of each other: canary,
/// then early, then broad as the rest. Defaults are PROPOSED (section 16.11): canary 3 percent (the doc says 1 to 5), early 25 percent.
/// </summary>
public sealed record RingOptions
{
    /// <summary>The fleet share of the canary ring in basis points. Default 300.</summary>
    public int CanaryBasisPoints { get; init; } = 300;

    /// <summary>The fleet share of the early ring in basis points. Default 2500.</summary>
    public int EarlyBasisPoints { get; init; } = 2500;

    /// <summary>The largest canary share a setting may ask for (10 percent), so the canary stays small.</summary>
    public const int MaxCanaryBasisPoints = 1000;

    /// <summary>The largest combined canary and early share (60 percent), so a broad ring always exists.</summary>
    public const int MaxCombinedBasisPoints = 6000;

    /// <summary>Checks every value. A bad value throws.</summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(CanaryBasisPoints, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(CanaryBasisPoints, MaxCanaryBasisPoints);
        ArgumentOutOfRangeException.ThrowIfLessThan(EarlyBasisPoints, CanaryBasisPoints);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(CanaryBasisPoints + EarlyBasisPoints, MaxCombinedBasisPoints);
    }
}

/// <summary>
/// Deterministic ring membership (doc 46 section 16.6). A device's ring for a release comes from a stable hash of the release id and the
/// device id, so every caller agrees, nothing is stored, and a device is in exactly one ring. Different releases reshuffle the fleet, so
/// the same devices are not always the canary.
/// </summary>
public static class ReleaseRingAssigner
{
    private const int _buckets = 10_000;

    /// <summary>The ring of one device. A small fleet may have an empty canary by chance; <see cref="AssignFleet"/> repairs that.</summary>
    public static ReleaseRing RingFor(string releaseId, string deviceId, RingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        return RingOfBucket(Bucket(releaseId, deviceId), options);
    }

    /// <summary>
    /// Assigns a whole fleet. When the fleet is not empty but the hash gave the canary nobody, the device with the lowest hash joins the
    /// canary, so a release always has a canary to watch. Duplicate device ids are counted once.
    /// </summary>
    public static IReadOnlyDictionary<string, ReleaseRing> AssignFleet(string releaseId, IEnumerable<string> deviceIds, RingOptions options)
    {
        ArgumentNullException.ThrowIfNull(deviceIds);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        Dictionary<string, ReleaseRing> result = new(StringComparer.Ordinal);
        string? lowest = null;
        ulong lowestHash = ulong.MaxValue;
        foreach (string deviceId in deviceIds)
        {
            if (result.ContainsKey(deviceId))
            {
                continue;
            }

            ulong hash = Hash(releaseId, deviceId);
            result[deviceId] = RingOfBucket((int)(hash % _buckets), options);
            if (hash < lowestHash || (hash == lowestHash && string.CompareOrdinal(deviceId, lowest) < 0))
            {
                lowest = deviceId;
                lowestHash = hash;
            }
        }

        if (lowest is not null && !result.ContainsValue(ReleaseRing.Canary))
        {
            result[lowest] = ReleaseRing.Canary;
        }

        return result;
    }

    private static ReleaseRing RingOfBucket(int bucket, RingOptions options) =>
        bucket < options.CanaryBasisPoints ? ReleaseRing.Canary
        : bucket < options.CanaryBasisPoints + options.EarlyBasisPoints ? ReleaseRing.Early
        : ReleaseRing.Broad;

    private static int Bucket(string releaseId, string deviceId) => (int)(Hash(releaseId, deviceId) % _buckets);

    private static ulong Hash(string releaseId, string deviceId)
    {
        ArgumentException.ThrowIfNullOrEmpty(releaseId);
        ArgumentException.ThrowIfNullOrEmpty(deviceId);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{releaseId}\n{deviceId}"));
        return BinaryPrimitives.ReadUInt64BigEndian(hash);
    }
}
