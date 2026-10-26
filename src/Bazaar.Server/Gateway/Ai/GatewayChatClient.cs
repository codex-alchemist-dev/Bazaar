using System.Runtime.CompilerServices;
using System.Text;
using Bazaar.Contracts.Gateway;
using Microsoft.Extensions.AI;

namespace Bazaar.Server.Gateway.Ai;

/// <summary>
/// A Microsoft.Extensions.AI <see cref="IChatClient"/> over <see cref="IModelGateway"/> (docs/design/05-model-gateway.md),
/// so Microsoft libraries can use the gateway. The agent id and route are fixed per instance; <see cref="ChatOptions.ModelId"/>
/// may replace the route for one call. The adapter holds no provider: every call goes through the gateway, which enforces policy.
/// A gateway failure is thrown as <see cref="GatewayModelException"/>, after the updates that came before it.
/// </summary>
public sealed class GatewayChatClient : IChatClient
{
    private readonly IModelGateway _gateway;
    private readonly string _agentId;
    private readonly string _route;
    private readonly ChatClientMetadata _metadata;

    public GatewayChatClient(IModelGateway gateway, string agentId, string route, string? providerName = "bazaar-gateway")
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(route);

        _gateway = gateway;
        _agentId = agentId;
        _route = route;
        _metadata = new ChatClientMetadata(providerName, null, route);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var request = GatewayChatMapping.ToModelRequest(_agentId, _route, messages, options);
        var pendingCalls = new Dictionary<string, PendingCall>();
        ChatFinishReason? finishReason = null;
        string? servedModel = null;

        await foreach (var modelEvent in _gateway.CallAsync(request, cancellationToken).ConfigureAwait(false))
        {
            switch (modelEvent)
            {
                case ModelEvent.TextDelta text:
                    yield return Update(new TextContent(text.Text));
                    break;

                case ModelEvent.ThinkingDelta thinking:
                    yield return Update(new TextReasoningContent(thinking.Text));
                    break;

                case ModelEvent.ToolCallStart start:
                    if (!pendingCalls.TryAdd(start.CallId, new PendingCall(start.Name, start.ProviderData)))
                    {
                        throw ProtocolError($"Tool call '{start.CallId}' started twice.");
                    }

                    break;

                case ModelEvent.ToolCallArgsDelta delta:
                    FindPending(pendingCalls, delta.CallId).Arguments.Append(delta.ArgumentsFragment);
                    break;

                case ModelEvent.ToolCallEnd end:
                    var call = FindPending(pendingCalls, end.CallId);
                    pendingCalls.Remove(end.CallId);
                    var arguments = GatewayChatMapping.ParseArguments(end.CallId, call.Arguments.ToString());
                    var functionCall = new FunctionCallContent(end.CallId, call.Name, arguments);
                    GatewayChatMapping.SetProviderData(functionCall, call.ProviderData);
                    yield return Update(functionCall);
                    break;

                case ModelEvent.UsageReported usage:
                    yield return Update(new UsageContent(GatewayChatMapping.ToUsageDetails(usage.Usage)));
                    break;

                case ModelEvent.Done done:
                    finishReason = GatewayChatMapping.ToFinishReason(done.StopReason);
                    servedModel = done.ServedModel;
                    break;

                case ModelEvent.Failure failure:
                    throw new GatewayModelException(failure.Code, failure.Message, failure.RetryAfterSeconds);

                case ModelEvent.Warning:
                    // Non-fatal notices do not change the response.
                    break;

                default:
                    throw ProtocolError($"Unknown model event '{modelEvent.GetType().Name}'.");
            }
        }

        // Usage can arrive after Done, so the final update waits for the end of the stream.
        if (pendingCalls.Count > 0)
        {
            throw new GatewayModelException("stream_dropped", $"The stream ended while tool call '{pendingCalls.Keys.First()}' was open.");
        }

        if (finishReason is null)
        {
            throw new GatewayModelException("stream_dropped", "The stream ended without a done event.");
        }

        yield return new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            FinishReason = finishReason,
            ModelId = servedModel,
        };
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return await GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken).ConfigureAwait(false);
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        if (serviceKey is not null)
        {
            return null;
        }

        if (serviceType == typeof(ChatClientMetadata))
        {
            return _metadata;
        }

        return serviceType.IsInstanceOfType(this) ? this : null;
    }

    public void Dispose()
    {
        // The adapter owns no resources; the gateway is owned by the host.
    }

    private static ChatResponseUpdate Update(AIContent content) => new(ChatRole.Assistant, [content]);

    private static PendingCall FindPending(Dictionary<string, PendingCall> pendingCalls, string callId)
    {
        if (!pendingCalls.TryGetValue(callId, out var call))
        {
            throw ProtocolError($"Tool call '{callId}' has no start event.");
        }

        return call;
    }

    private static GatewayModelException ProtocolError(string message) => new("provider_error", message);

    private sealed record PendingCall(string Name, string? ProviderData)
    {
        public StringBuilder Arguments { get; } = new();
    }
}
