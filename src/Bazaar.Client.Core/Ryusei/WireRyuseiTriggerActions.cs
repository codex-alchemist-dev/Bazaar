using Bazaar.Client.Core.Wire;
using Bazaar.Contracts.Ryusei;
using Bazaar.Contracts.Wire;

namespace Bazaar.Client.Core.Ryusei;

/// <summary>
/// Pause and resume over the wire (doc 38, Layer 2): sends <c>ryusei.trigger.pause</c> or <c>ryusei.trigger.resume</c> for
/// one trigger. The server decides authority and state. A refusal or a lost connection surfaces as the thrown
/// <see cref="WireRequestException"/>, so the pane keeps the row as it was.
/// </summary>
public sealed class WireRyuseiTriggerActions(IWireRequester requests) : IRyuseiTriggerActions
{
    /// <inheritdoc />
    public Task PauseAsync(string triggerId, CancellationToken ct) =>
        SendAsync(WireTypes.RyuseiPause, triggerId, ct);

    /// <inheritdoc />
    public Task ResumeAsync(string triggerId, CancellationToken ct) =>
        SendAsync(WireTypes.RyuseiResume, triggerId, ct);

    private async Task SendAsync(string type, string triggerId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(triggerId);
        if (!Guid.TryParse(triggerId, out var id))
        {
            throw new ArgumentException("A trigger id must be a GUID.", nameof(triggerId));
        }

        await requests.RequestAsync<RyuseiTriggerChangeRequest, RyuseiTriggerChangeReply>(
            type, new RyuseiTriggerChangeRequest(id), ct).ConfigureAwait(false);
    }
}
