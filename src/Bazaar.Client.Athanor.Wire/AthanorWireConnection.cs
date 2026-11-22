using Bazaar.Client.Abstractions.Wire;
using Bazaar.Contracts.Wire.Codec;

namespace Bazaar.Client.Athanor.Wire;

/// <summary>
/// The editor's own connection to the company server (doc 57 §2.2): a <see cref="WireClient"/> on the head's endpoint and
/// credential that carries only <c>editor.open</c> and <c>editor.save</c>, started on first use and cancelled on dispose.
/// <see cref="Source"/> bounds every request by a timeout, so an unreachable server shows as unavailable instead of
/// hanging the editor.
/// </summary>
public sealed class AthanorWireConnection : IAsyncDisposable
{
    /// <summary>The default bound on one request.</summary>
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(15);

    private readonly WireClient _client;
    private readonly CancellationTokenSource _cts = new();
    private readonly Lock _gate = new();
    private Task? _run;

    /// <param name="client">A client built on the head's transport. The connection runs it; the caller must not.</param>
    /// <param name="requestTimeout">The bound on one request; <see cref="DefaultRequestTimeout"/> when null.</param>
    public AthanorWireConnection(WireClient client, TimeSpan? requestTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        Source = new TimeBoundEditorFileSource(new WireEditorFileSource(client), new StartOnUse(this), requestTimeout ?? DefaultRequestTimeout);
    }

    /// <summary>Builds a connection over the JSON WebSocket transport.</summary>
    /// <param name="server">The socket URL.</param>
    /// <param name="token">The credential sent in <c>hello</c>.</param>
    /// <param name="deviceClass">Device class such as <c>windows</c> or <c>linux</c>.</param>
    /// <param name="clientBuild">Build label sent in <c>hello</c>.</param>
    public static AthanorWireConnection Create(Uri server, string token, string deviceClass, string clientBuild, TimeSpan? requestTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(server);
        var client = new WireClient(
            new WebSocketWireTransportFactory(server),
            new JsonWireCodec(),
            new InMemoryCursorStore(),
            new WireClientOptions
            {
                DeviceId = $"{deviceClass}-{Environment.MachineName}-athanor",
                DeviceClass = deviceClass,
                ClientBuild = clientBuild,
                Credential = token,
            },
            TimeProvider.System);
        return new AthanorWireConnection(client, requestTimeout);
    }

    /// <summary>The file source over this connection. Safe to hand to the pane.</summary>
    public IEditorFileSource Source { get; }

    /// <summary>True once the connection loop was started.</summary>
    public bool Started
    {
        get
        {
            lock (_gate)
            {
                return _run is not null;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? run;
        lock (_gate)
        {
            run = _run;
        }

        await _cts.CancelAsync().ConfigureAwait(false);
        if (run is not null)
        {
            try
            {
                await run.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // the loop ends by cancellation
            }
        }

        _cts.Dispose();
    }

    private void EnsureStarted()
    {
        lock (_gate)
        {
            _run ??= Task.Run(() => _client.RunAsync(_cts.Token));
        }
    }

    private sealed class StartOnUse(AthanorWireConnection owner)
    {
        public void Touch() => owner.EnsureStarted();
    }

    /// <summary>Starts the connection on the first request and ends every request after the timeout.</summary>
    private sealed class TimeBoundEditorFileSource(IEditorFileSource inner, StartOnUse start, TimeSpan timeout) : IEditorFileSource
    {
        public async Task<EditorDocument?> OpenAsync(EditorDocumentId id, CancellationToken cancellationToken)
        {
            start.Touch();
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(timeout);
            try
            {
                return await inner.OpenAsync(id, limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new EditorFileException(EditorErrorCodes.Unavailable, "The server did not answer in time.");
            }
        }

        public async Task<SaveOutcome> SaveAsync(EditorDocumentId id, string content, string baseRevision, CancellationToken cancellationToken)
        {
            start.Touch();
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(timeout);
            try
            {
                return await inner.SaveAsync(id, content, baseRevision, limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new SaveOutcome.Unavailable("The server did not answer in time.");
            }
        }
    }
}
