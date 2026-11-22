namespace Bazaar.Contracts.Wire;

/// <summary>
/// Marks a record as a wire payload (doc 90 section 3). The codec generators read the marker from this assembly and
/// emit one closed registry of payload types plus the serialisation code for each codec. Dependency-free on purpose:
/// <c>Bazaar.Contracts</c> carries no codec attributes or codec packages.
/// </summary>
/// <remarks>
/// A record used as the payload of one or more message types names each type with <c>[WirePayload("gc.send")]</c>
/// (see <see cref="WireTypes"/>). A record that is only the reply to a request, or a shape shared by several messages,
/// has no message type of its own and uses the parameterless form. Records that are only nested inside a payload
/// need no marker; the generators follow the property types.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = true, Inherited = false)]
public sealed class WirePayloadAttribute : Attribute
{
    /// <summary>Marks a payload that has no message type of its own.</summary>
    public WirePayloadAttribute()
    {
    }

    /// <summary>Marks a payload carried by the message type <paramref name="messageType"/>.</summary>
    /// <param name="messageType">The envelope <c>t</c> value, such as <c>gc.send</c>.</param>
    public WirePayloadAttribute(string messageType)
    {
        MessageType = messageType;
    }

    /// <summary>The envelope <c>t</c> value this payload travels under, or null for a reply or shared shape.</summary>
    public string? MessageType { get; }
}
