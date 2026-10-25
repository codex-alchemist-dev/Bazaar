using Bazaar.Contracts.Gateway;

namespace Bazaar.Server.Gateway.Fakes;

/// <summary>
/// One step of a scripted model call. Steps play in order. A failure, a <c>Done</c> or any fault step ends the
/// call, so it must be the last step (<see cref="FakeProvider.EnqueueCall"/> checks this).
/// </summary>
public abstract record ScriptStep
{
    /// <summary>Yield an event exactly as an adapter would. A wrong served model is a <c>Done</c> with another name.</summary>
    public sealed record Emit(ModelEvent Event) : ScriptStep;

    /// <summary>The upstream answered 429 with an optional Retry-After.</summary>
    public sealed record RateLimited(int? RetryAfterSeconds = null) : ScriptStep;

    /// <summary>The upstream answered 429 for a spend cap. Retry-After does not apply.</summary>
    public sealed record SpendCapped : ScriptStep;

    /// <summary>
    /// The upstream stayed silent for <c>After</c>, measured on the provider's clock, and the call timed out.
    /// </summary>
    public sealed record TimedOut(TimeSpan After) : ScriptStep;

    /// <summary>
    /// The model started a tool call whose argument JSON is not valid. The stream carries the raw fragment,
    /// then the failure <c>malformed_tool_json</c>.
    /// </summary>
    public sealed record MalformedToolJson(string CallId, string Name, string RawArguments) : ScriptStep;

    /// <summary>The connection dropped mid-stream. Whatever came before stays; nothing after it arrives.</summary>
    public sealed record ConnectionDropped : ScriptStep;
}
