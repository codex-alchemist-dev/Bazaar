using Bazaar.Contracts.Engines;

namespace Bazaar.Engines.Adapters;

/// <summary>
/// A failure of an adapter request or of the adapter session. <see cref="Code"/> is an <see cref="EngineErrorCodes"/>
/// value (or the adapter's own JSON-RPC error code); <see cref="LastOutput"/> holds the most recent output lines so the
/// caller can report what the adapter printed before it failed.
/// </summary>
public sealed class EngineAdapterException : Exception
{
    public EngineAdapterException(int code, string message, IReadOnlyList<string>? lastOutput = null)
        : base(message)
    {
        Code = code;
        LastOutput = lastOutput ?? [];
    }

    public int Code { get; }

    /// <summary>The last output lines (oldest first) received from the adapter, including stderr lines.</summary>
    public IReadOnlyList<string> LastOutput { get; }

    /// <summary>A copy with the same code, message and output, so each awaiting caller gets its own exception object.</summary>
    internal EngineAdapterException Copy() => new(Code, Message, LastOutput);
}
