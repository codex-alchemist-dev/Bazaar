using System.Net;

namespace Bazaar.Conduit.LanMdns;

/// <summary>
/// Builds the mDNS answers and announcements for one service instance. Pure: it never touches a socket, so the module
/// and the tests share one implementation.
/// </summary>
internal sealed class MdnsResponder
{
    /// <summary>The DNS-SD service type Bazaar advertises.</summary>
    public const string ServiceType = "_bazaar._tcp.local";

    private const uint _ptrTtl = 4500;
    private const uint _hostTtl = 120;

    private readonly ushort _port;
    private readonly IReadOnlyList<string> _txt;
    private readonly IReadOnlyList<IPAddress> _addresses;

    /// <summary>Creates the responder. <paramref name="instanceLabel"/> must be one valid DNS label.</summary>
    public MdnsResponder(string instanceLabel, ushort port, IReadOnlyList<string> txt, IReadOnlyList<IPAddress> addresses)
    {
        ArgumentException.ThrowIfNullOrEmpty(instanceLabel);
        ArgumentNullException.ThrowIfNull(txt);
        ArgumentNullException.ThrowIfNull(addresses);

        InstanceName = $"{instanceLabel}.{ServiceType}";
        HostName = $"{instanceLabel}.local";
        _port = port;
        _txt = txt;
        _addresses = addresses;
    }

    /// <summary>Full instance name, for example <c>bazaar._bazaar._tcp.local</c>.</summary>
    public string InstanceName { get; }

    /// <summary>Host name the SRV record points at.</summary>
    public string HostName { get; }

    /// <summary>
    /// Returns the response to a query, or null when the query asks for nothing this instance owns. A PTR question on the
    /// service type gets the PTR with SRV, TXT and A records as additionals. Instance and host questions get their records.
    /// </summary>
    public MdnsMessage? Respond(MdnsMessage query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.IsResponse)
        {
            return null;
        }

        var answers = new List<MdnsResourceRecord>();
        var additionals = new List<MdnsResourceRecord>();
        foreach (var question in query.Questions)
        {
            if (question.Class != MdnsTypes.ClassIn)
            {
                continue;
            }

            if (Same(question.Name, ServiceType) && question.Type is MdnsTypes.PtrRecordType or MdnsTypes.Any)
            {
                answers.Add(Ptr(_ptrTtl));
                additionals.Add(Srv(_hostTtl));
                additionals.Add(Txt(_hostTtl));
                additionals.AddRange(Addresses(_hostTtl));
            }
            else if (Same(question.Name, InstanceName) && question.Type is MdnsTypes.Srv or MdnsTypes.Txt or MdnsTypes.Any)
            {
                if (question.Type is MdnsTypes.Srv or MdnsTypes.Any)
                {
                    answers.Add(Srv(_hostTtl));
                }

                if (question.Type is MdnsTypes.Txt or MdnsTypes.Any)
                {
                    answers.Add(Txt(_hostTtl));
                }

                additionals.AddRange(Addresses(_hostTtl));
            }
            else if (Same(question.Name, HostName) && question.Type is MdnsTypes.A or MdnsTypes.Any)
            {
                answers.AddRange(Addresses(_hostTtl));
            }
        }

        if (answers.Count == 0)
        {
            return null;
        }

        var distinctAnswers = answers.Distinct().ToList();
        var extra = additionals.Distinct().Except(distinctAnswers).ToList();
        return new MdnsMessage(0, true, true, [], distinctAnswers, [], extra);
    }

    /// <summary>The full set of records. A goodbye sends the same records with TTL zero (RFC 6762 section 10.1).</summary>
    public MdnsMessage Advertise(bool goodbye)
    {
        var ptrTtl = goodbye ? 0 : _ptrTtl;
        var hostTtl = goodbye ? 0 : _hostTtl;
        var answers = new List<MdnsResourceRecord> { Ptr(ptrTtl), Srv(hostTtl), Txt(hostTtl) };
        answers.AddRange(Addresses(hostTtl));
        return new MdnsMessage(0, true, true, [], answers, [], []);
    }

    private static bool Same(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private MdnsResourceRecord Ptr(uint ttl) =>
        new(ServiceType, MdnsTypes.ClassIn, false, ttl, new PtrRecordData(InstanceName));

    private MdnsResourceRecord Srv(uint ttl) =>
        new(InstanceName, MdnsTypes.ClassIn, true, ttl, new SrvRecordData(0, 0, _port, HostName));

    private MdnsResourceRecord Txt(uint ttl) =>
        new(InstanceName, MdnsTypes.ClassIn, true, ttl, new TxtRecordData(_txt));

    private IEnumerable<MdnsResourceRecord> Addresses(uint ttl) =>
        _addresses.Select(address => new MdnsResourceRecord(HostName, MdnsTypes.ClassIn, true, ttl, new ARecordData(address)));
}
