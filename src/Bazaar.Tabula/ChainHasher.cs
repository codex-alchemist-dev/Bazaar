using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Bazaar.Tabula;

/// <summary>
/// Canonical hashing and appending for named chains. Pure: no I/O, no storage. The hash is SHA-256 over a domain string
/// and length-prefixed fields, so no two different field tuples share an encoding. The chain id is part of the hash, so an
/// entry cannot be moved between chains.
/// </summary>
public static class ChainHasher
{
    /// <summary>Signing and hashing domain for new chains (doc 85 audit TB4). A host with older history passes its own domain.</summary>
    public const string DefaultDomain = "cdax.tabula.entry.v1";

    /// <summary>Length of every hash, in bytes.</summary>
    public const int HashLength = 32;

    /// <summary><c>PrevHash</c> of the first entry in any chain. A new array on every call.</summary>
    public static byte[] GenesisHash() => new byte[HashLength];

    /// <summary>Builds the next entry after <paramref name="head"/> (null for an empty chain). Seq is head.Seq + 1, or 1.</summary>
    /// <exception cref="ArgumentException"><paramref name="head"/> belongs to another chain.</exception>
    public static ChainEntry Append(
        ChainId chain,
        ChainEntry? head,
        DateTimeOffset ts,
        string actor,
        string? onBehalfOf,
        string kind,
        string payload,
        string domain = DefaultDomain)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(payload);
        if (head is not null && head.Chain != chain)
        {
            throw new ArgumentException("Head belongs to a different chain.", nameof(head));
        }

        long seq = head is null ? 1 : head.Seq + 1;
        byte[] prev = head is null ? GenesisHash() : head.Hash;
        byte[] hash = ComputeHash(chain, seq, ts, actor, onBehalfOf, kind, payload, prev, domain);
        return new ChainEntry(chain, seq, ts, actor, onBehalfOf, kind, payload, prev, hash);
    }

    /// <summary>Recomputes the hash an entry should carry.</summary>
    public static byte[] ComputeHash(ChainEntry entry, string domain = DefaultDomain)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return ComputeHash(entry.Chain, entry.Seq, entry.Ts, entry.Actor, entry.OnBehalfOf, entry.Kind, entry.Payload, entry.PrevHash, domain);
    }

    public static byte[] ComputeHash(
        ChainId chain,
        long seq,
        DateTimeOffset ts,
        string actor,
        string? onBehalfOf,
        string kind,
        string payload,
        byte[] prevHash,
        string domain = DefaultDomain)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(prevHash);
        ArgumentNullException.ThrowIfNull(domain);

        using MemoryStream stream = new();
        Span<byte> number = stackalloc byte[8];

        WriteField(stream, domain);
        WriteField(stream, chain.Value);
        BinaryPrimitives.WriteInt64BigEndian(number, seq);
        stream.Write(number);
        BinaryPrimitives.WriteInt64BigEndian(number, ts.ToUniversalTime().UtcTicks);
        stream.Write(number);
        WriteField(stream, actor);

        // A presence byte keeps "no delegate" distinct from an empty-string delegate.
        stream.WriteByte(onBehalfOf is null ? (byte)0 : (byte)1);
        if (onBehalfOf is not null)
        {
            WriteField(stream, onBehalfOf);
        }

        WriteField(stream, kind);
        WriteField(stream, payload);
        WriteField(stream, prevHash);

        return SHA256.HashData(stream.ToArray());
    }

    private static void WriteField(MemoryStream stream, string value) => WriteField(stream, Encoding.UTF8.GetBytes(value));

    private static void WriteField(MemoryStream stream, byte[] value)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, value.Length);
        stream.Write(length);
        stream.Write(value);
    }
}
