using System.Net;

namespace Bazaar.Conduit.LanMdns;

/// <summary>DNS record types and classes used by mDNS (RFC 1035, RFC 6762).</summary>
public static class MdnsTypes
{
    /// <summary>IPv4 host address.</summary>
    public const ushort A = 1;

    /// <summary>Domain name pointer.</summary>
    public const ushort PtrRecordType = 12;

    /// <summary>Text strings.</summary>
    public const ushort Txt = 16;

    /// <summary>Service locator.</summary>
    public const ushort Srv = 33;

    /// <summary>Wildcard, questions only.</summary>
    public const ushort Any = 255;

    /// <summary>Internet class.</summary>
    public const ushort ClassIn = 1;

    /// <summary>Cache-flush bit on records, unicast-response bit on questions.</summary>
    public const ushort TopBit = 0x8000;
}

/// <summary>One question of an mDNS message.</summary>
/// <param name="Name">Dotted name, for example <c>_bazaar._tcp.local</c>.</param>
/// <param name="Type">Record type asked for.</param>
/// <param name="Class">Class without the unicast-response bit.</param>
/// <param name="UnicastResponse">True when the querier asked for a unicast reply.</param>
public sealed record MdnsQuestion(string Name, ushort Type, ushort Class, bool UnicastResponse = false);

/// <summary>Typed rdata of a resource record.</summary>
public abstract record MdnsRecordData
{
    /// <summary>Record type the data belongs to.</summary>
    public abstract ushort Type { get; }
}

/// <summary>PTR rdata: the target name.</summary>
/// <param name="Target">Target name.</param>
public sealed record PtrRecordData(string Target) : MdnsRecordData
{
    /// <inheritdoc />
    public override ushort Type => MdnsTypes.PtrRecordType;
}

/// <summary>SRV rdata.</summary>
/// <param name="Priority">Priority.</param>
/// <param name="Weight">Weight.</param>
/// <param name="Port">Service port.</param>
/// <param name="Target">Host name.</param>
public sealed record SrvRecordData(ushort Priority, ushort Weight, ushort Port, string Target) : MdnsRecordData
{
    /// <inheritdoc />
    public override ushort Type => MdnsTypes.Srv;
}

/// <summary>TXT rdata: a list of <c>key=value</c> strings, each at most 255 bytes.</summary>
/// <param name="Strings">The strings.</param>
public sealed record TxtRecordData(IReadOnlyList<string> Strings) : MdnsRecordData
{
    /// <inheritdoc />
    public override ushort Type => MdnsTypes.Txt;
}

/// <summary>A rdata: one IPv4 address.</summary>
/// <param name="Address">The address.</param>
public sealed record ARecordData(IPAddress Address) : MdnsRecordData
{
    /// <inheritdoc />
    public override ushort Type => MdnsTypes.A;
}

/// <summary>Any other record type, kept as raw bytes so unknown records can be skipped.</summary>
/// <param name="RecordType">Record type number.</param>
/// <param name="Bytes">Raw rdata.</param>
public sealed record RawRecordData(ushort RecordType, byte[] Bytes) : MdnsRecordData
{
    /// <inheritdoc />
    public override ushort Type => RecordType;
}

/// <summary>One resource record.</summary>
/// <param name="Name">Owner name.</param>
/// <param name="Class">Class without the cache-flush bit.</param>
/// <param name="CacheFlush">True for records unique to the name (SRV, TXT, A).</param>
/// <param name="Ttl">Time to live in seconds. Zero is a goodbye.</param>
/// <param name="Data">Typed rdata.</param>
public sealed record MdnsResourceRecord(string Name, ushort Class, bool CacheFlush, uint Ttl, MdnsRecordData Data)
{
    /// <summary>Record type, taken from the rdata.</summary>
    public ushort Type => Data.Type;
}

/// <summary>A decoded or to-be-encoded mDNS message.</summary>
/// <param name="Id">Transaction id. Zero on multicast responses.</param>
/// <param name="IsResponse">True for responses (QR bit).</param>
/// <param name="Authoritative">True when the sender owns the records (AA bit).</param>
/// <param name="Questions">Questions.</param>
/// <param name="Answers">Answer records.</param>
/// <param name="Authorities">Authority records.</param>
/// <param name="Additionals">Additional records.</param>
public sealed record MdnsMessage(
    ushort Id,
    bool IsResponse,
    bool Authoritative,
    IReadOnlyList<MdnsQuestion> Questions,
    IReadOnlyList<MdnsResourceRecord> Answers,
    IReadOnlyList<MdnsResourceRecord> Authorities,
    IReadOnlyList<MdnsResourceRecord> Additionals);
