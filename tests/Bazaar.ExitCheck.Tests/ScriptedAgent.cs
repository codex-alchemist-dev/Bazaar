using Bazaar.Client.Core.Wire;
using Bazaar.Contracts.Gateway;
using Bazaar.Contracts.Wire;
using Bazaar.Contracts.Wire.Codec;
using Bazaar.Server.Gateway.Ai;
using Bazaar.Server.Gateway.Fakes;
using Microsoft.Extensions.AI;

namespace Bazaar.ExitCheck.Tests;

/// <summary>
/// A fake agent: asks its model through the policy-checked gateway (GW10 <see cref="GatewayChatClient"/>), then posts the
/// answer to the GC over its own socket. It is the exit check's only writer, so every message in the log came from a model call.
/// </summary>
public sealed class ScriptedAgent
{
    private readonly Guid _gcId;
    private readonly FakeProvider _provider;
    private readonly WireClient _client;
    private readonly PolicyGateGateway _gateway;
    private readonly string _route;

    public ScriptedAgent(Guid gcId, WireClient client, FakeProvider provider, PolicyGateGateway gateway, string route)
    {
        _gcId = gcId;
        _client = client;
        _provider = provider;
        _gateway = gateway;
        _route = route;
    }

    /// <summary>Scripts one model answer, asks for it and posts it. Throws <see cref="GatewayModelException"/> when policy denies the call.</summary>
    public async Task<GcSendResult> SayAsync(string text, CancellationToken cancellationToken)
    {
        _provider.EnqueueCall(
            new ScriptStep.Emit(new ModelEvent.TextDelta(text)),
            new ScriptStep.Emit(new ModelEvent.Done(StopReason.EndTurn, ExitCheckPolicyWorld.AllowedModel)));
        using var chat = new GatewayChatClient(_gateway, ExitCheckPolicyWorld.AgentId, _route);
        var answer = await chat.GetResponseAsync([new ChatMessage(ChatRole.User, "say something")], cancellationToken: cancellationToken).ConfigureAwait(false);
        return await _client.RequestAsync<GcSend, GcSendResult>(WireTypes.GcSend, new GcSend(_gcId, answer.Text, null, null), cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>One device: a <see cref="WireClient"/> on a severable transport, running until disposed.</summary>
public sealed class ExitCheckDevice : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _run;

    public ExitCheckDevice(IExitCheckServer server, Guid principal, string deviceId)
    {
        Transport = new SeveringTransport(server.Transport);
        Cursors = new InMemoryCursorStore();
        Client = new WireClient(Transport, new JsonWireCodec(), Cursors, new WireClientOptions
        {
            DeviceId = deviceId,
            DeviceClass = "test",
            ClientBuild = "exit-check",
            Credential = server.CredentialFor(principal),
            BackoffStart = TimeSpan.FromMilliseconds(20),
            BackoffMax = TimeSpan.FromMilliseconds(200),
            Jitter = () => 0,
        }, TimeProvider.System);
        _run = Client.RunAsync(_cts.Token);
    }

    public SeveringTransport Transport { get; }

    public InMemoryCursorStore Cursors { get; }

    public WireClient Client { get; }

    /// <summary>Reads the next <paramref name="count"/> messages the client applied, failing if they do not arrive.</summary>
    public async Task<List<WireGcMessage>> ReadAsync(int count, CancellationToken cancellationToken)
    {
        var got = new List<WireGcMessage>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while (got.Count < count)
        {
            got.Add(await Client.Messages.ReadAsync(timeout.Token).ConfigureAwait(false));
        }

        return got;
    }

    /// <summary>True when nothing more arrives within <paramref name="quiet"/>: no late duplicate, no extra entry.</summary>
    public async Task<bool> StaysQuietAsync(TimeSpan quiet, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(quiet);
        try
        {
            await Client.Messages.WaitToReadAsync(timeout.Token).ConfigureAwait(false);
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return true;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        await _run.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        _cts.Dispose();
    }
}
