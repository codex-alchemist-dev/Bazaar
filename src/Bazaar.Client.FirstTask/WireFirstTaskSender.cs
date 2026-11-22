using Bazaar.Client.Abstractions.Server;
using Bazaar.Client.Abstractions.Wire;
using Bazaar.Contracts.Wire;
using Bazaar.Contracts.Wire.Codec;

namespace Bazaar.Client.FirstTask;

/// <summary>
/// Posts the first task as an ordinary <c>gc.send</c> that mentions the first agent (doc 45 section 2.10). It opens its own short
/// wire connection with the signed-in person's access token, sends once, and closes, so it needs no running chat. The token is
/// read per send and never logged; any failure is one generic outcome.
/// </summary>
public sealed class WireFirstTaskSender(
    Func<IWireTransportFactory> transport,
    Func<Task<string?>> accessToken,
    string deviceClass,
    string clientBuild,
    TimeProvider? time = null) : IFirstTaskSender
{
    private static readonly TimeSpan _budget = TimeSpan.FromSeconds(15);

    /// <summary>The sender for a company server: the same address as the HTTP calls, as <c>ws</c> or <c>wss</c>, on the wire path.</summary>
    public static WireFirstTaskSender For(ServerEndpoint endpoint, Func<Task<string?>> accessToken, string deviceClass, string clientBuild)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var builder = new UriBuilder(endpoint.BaseAddress) { Path = "/ws", Query = "", Fragment = "" };
        builder.Scheme = endpoint.BaseAddress.Scheme == Uri.UriSchemeHttps ? Uri.UriSchemeWss : Uri.UriSchemeWs;
        return new WireFirstTaskSender(() => new WebSocketWireTransportFactory(builder.Uri), accessToken, deviceClass, clientBuild);
    }

    public async Task<FirstTaskOutcome> SendAsync(FirstTaskTarget target, string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (await accessToken().ConfigureAwait(false) is not { Length: > 0 } token) return FirstTaskOutcome.Failed;

        var client = new WireClient(
            transport(),
            new JsonWireCodec(),
            new InMemoryCursorStore(),
            new WireClientOptions
            {
                DeviceId = $"{deviceClass}-{Environment.MachineName}",
                DeviceClass = deviceClass,
                ClientBuild = clientBuild,
                Credential = token,
            },
            time ?? TimeProvider.System);

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var run = client.RunAsync(stop.Token);
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(_budget);
            await client.RequestAsync<GcSend, GcSendResult>(WireTypes.GcSend, new GcSend(target.ChatId, text, [target.AgentId], null), budget.Token).ConfigureAwait(false);
            return FirstTaskOutcome.Sent;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return FirstTaskOutcome.Failed;
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            try
            {
                await run.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The connection is already finished with; a late failure changes nothing.
            }
        }
    }
}
