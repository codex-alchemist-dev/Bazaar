namespace Bazaar.Contracts.Wire;

/// <summary>The <c>k</c> field of an envelope. Wire names are lower case: <c>req</c>, <c>res</c>, <c>evt</c>, <c>err</c>.</summary>
public enum EnvelopeKind
{
    /// <summary>A request; gets exactly one response or error.</summary>
    Request,

    /// <summary>The successful answer to a request.</summary>
    Response,

    /// <summary>A server-originated change; no reply.</summary>
    Event,

    /// <summary>The failed answer to a request, carrying <see cref="WireError"/>.</summary>
    Error,
}

/// <summary>Wire names for <see cref="EnvelopeKind"/>.</summary>
public static class EnvelopeKinds
{
    /// <summary>Wire name of <see cref="EnvelopeKind.Request"/>.</summary>
    public const string Request = "req";

    /// <summary>Wire name of <see cref="EnvelopeKind.Response"/>.</summary>
    public const string Response = "res";

    /// <summary>Wire name of <see cref="EnvelopeKind.Event"/>.</summary>
    public const string Event = "evt";

    /// <summary>Wire name of <see cref="EnvelopeKind.Error"/>.</summary>
    public const string Error = "err";

    /// <summary>The wire name for a kind.</summary>
    public static string ToWire(EnvelopeKind kind) => kind switch
    {
        EnvelopeKind.Request => Request,
        EnvelopeKind.Response => Response,
        EnvelopeKind.Event => Event,
        EnvelopeKind.Error => Error,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>Parses a wire name; false for anything not listed above.</summary>
    public static bool TryParse(string? wire, out EnvelopeKind kind)
    {
        switch (wire)
        {
            case Request: kind = EnvelopeKind.Request; return true;
            case Response: kind = EnvelopeKind.Response; return true;
            case Event: kind = EnvelopeKind.Event; return true;
            case Error: kind = EnvelopeKind.Error; return true;
            default: kind = default; return false;
        }
    }
}
