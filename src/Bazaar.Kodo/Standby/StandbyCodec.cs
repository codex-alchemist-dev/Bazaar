using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bazaar.Kodo.Standby;

/// <summary>Source-generated JSON for standby records: camelCase, compact, nulls written, unknown members rejected.</summary>
[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(LeaseRecord))]
[JsonSerializable(typeof(StandbyState))]
internal sealed partial class StandbyJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Canonical serialisation and digests for the standby records: compact camelCase JSON in declaration order, UTC times, so one record
/// has one encoding. The digest is SHA-256 over those bytes. Nothing is signed.
/// </summary>
public static class StandbyCodec
{
    /// <summary>Length of a digest in bytes.</summary>
    public const int DigestLength = 32;

    /// <summary>The canonical UTF-8 JSON of a lease.</summary>
    /// <exception cref="ArgumentException">The lease is not well formed.</exception>
    public static byte[] Serialize(LeaseRecord lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        LeaseRecord canonical = lease with { GrantedAt = lease.GrantedAt.ToUniversalTime(), ExpiresAt = lease.ExpiresAt.ToUniversalTime() };
        return canonical.IsWellFormed
            ? JsonSerializer.SerializeToUtf8Bytes(canonical, StandbyJsonContext.Default.LeaseRecord)
            : throw new ArgumentException("Lease record is not well formed.", nameof(lease));
    }

    /// <summary>The canonical UTF-8 JSON of a state.</summary>
    /// <exception cref="ArgumentException">The state is not well formed.</exception>
    public static byte[] Serialize(StandbyState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        StandbyState canonical = state with { At = state.At.ToUniversalTime() };
        return canonical.IsWellFormed
            ? JsonSerializer.SerializeToUtf8Bytes(canonical, StandbyJsonContext.Default.StandbyState)
            : throw new ArgumentException("Standby state is not well formed.", nameof(state));
    }

    /// <summary>Parses a lease. Null for invalid JSON, unknown members or out-of-range values; never throws on bad data.</summary>
    public static LeaseRecord? TryDeserializeLease(ReadOnlySpan<byte> json)
    {
        try
        {
            LeaseRecord? lease = JsonSerializer.Deserialize(json, StandbyJsonContext.Default.LeaseRecord);
            return lease is { IsWellFormed: true } ? lease : null;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or FormatException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Parses a state. Null for invalid JSON, unknown members or out-of-range values; never throws on bad data.</summary>
    public static StandbyState? TryDeserializeState(ReadOnlySpan<byte> json)
    {
        try
        {
            StandbyState? state = JsonSerializer.Deserialize(json, StandbyJsonContext.Default.StandbyState);
            return state is { IsWellFormed: true } ? state : null;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or FormatException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>SHA-256 of the canonical bytes of a lease.</summary>
    public static byte[] Digest(LeaseRecord lease) => SHA256.HashData(Serialize(lease));

    /// <summary>SHA-256 of the canonical bytes of a state.</summary>
    public static byte[] Digest(StandbyState state) => SHA256.HashData(Serialize(state));
}
