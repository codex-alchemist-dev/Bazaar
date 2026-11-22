using Bazaar.Client.Components.Chat;
using Bazaar.Contracts.Wire;

namespace Bazaar.Client.FirstTask;

/// <summary>Posts the first task as an ordinary <c>gc.send</c> that mentions the first agent (doc 45 section 2.8).</summary>
public sealed class GcFirstTaskSender(IGcWire wire) : IFirstTaskSender
{
    public async Task<FirstTaskOutcome> SendAsync(FirstTaskTarget target, string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        try
        {
            await wire.SendAsync(new GcSend(target.ChatId, text, [target.AgentId], null), cancellationToken).ConfigureAwait(false);
            return FirstTaskOutcome.Sent;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return FirstTaskOutcome.Failed;
        }
    }
}
