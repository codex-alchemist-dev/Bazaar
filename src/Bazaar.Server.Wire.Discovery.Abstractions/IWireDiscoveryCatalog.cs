namespace Bazaar.Server.Wire.Discovery;

/// <summary>
/// What one installed transport or codec module advertises on the discovery route (doc 90 section 6). A module registers one
/// implementation in DI and lists only the ids it currently has enabled: the list is read on every request, so an operator
/// change shows up without a restart. Names are the ids of doc 90 (<c>ws</c>, <c>quic</c>, <c>webtransport</c>, <c>grpc</c>,
/// <c>grpc-web</c>, <c>json</c>, <c>cbor</c>, <c>msgpack</c>, <c>memorypack</c>, <c>protobuf</c>). No endpoint, host name or secret belongs here.
/// </summary>
public interface IWireDiscoveryContribution
{
    /// <summary>Transport ids this module serves and the operator has enabled.</summary>
    IReadOnlyList<string> Transports { get; }

    /// <summary>Codec ids this module serves and the operator has enabled.</summary>
    IReadOnlyList<string> Codecs { get; }
}

/// <summary>The merged view of every <see cref="IWireDiscoveryContribution"/>, which the discovery route publishes.</summary>
public interface IWireDiscoveryCatalog
{
    /// <summary>Enabled transport ids, distinct, in registration order.</summary>
    IReadOnlyList<string> Transports { get; }

    /// <summary>Enabled codec ids, distinct, in registration order.</summary>
    IReadOnlyList<string> Codecs { get; }
}
