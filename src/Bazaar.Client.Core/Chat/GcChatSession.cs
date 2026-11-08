using Bazaar.Client.Core.Wire;
using Bazaar.Contracts.Wire;

namespace Bazaar.Client.Core.Chat;

/// <summary>
/// Connects one group chat to a <see cref="WireClient"/>: subscribes, folds incoming messages into a
/// <see cref="ChatTimeline"/>, and sends. <see cref="Changed"/> is raised on a background thread; heads marshal it to
/// their UI thread.
/// </summary>
public sealed class GcChatSession : IAsyncDisposable
{
    private readonly WireClient _client;
    private readonly ChatConnectionSettings _settings;
    private readonly CancellationTokenSource _cts = new();
    private Task _run = Task.CompletedTask;
    private Task _pump = Task.CompletedTask;
    private string _status = "connecting";

    /// <param name="client">The wire client that carries the group chat's stream.</param>
    /// <param name="settings">Which group chat to show.</param>
    /// <param name="timeline">Holds the messages; the session fills it.</param>
    public GcChatSession(WireClient client, ChatConnectionSettings settings, ChatTimeline timeline)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        Timeline = timeline ?? throw new ArgumentNullException(nameof(timeline));
    }

    /// <summary>Raised after the timeline or the status changes.</summary>
    public event EventHandler? Changed;

    /// <summary>The messages of this group chat.</summary>
    public ChatTimeline Timeline { get; }

    /// <summary>A short status line, such as <c>connecting</c> or <c>closed</c>.</summary>
    public string Status
    {
        get => Volatile.Read(ref _status);
        private set => Volatile.Write(ref _status, value);
    }

    /// <summary>Subscribes to the group chat and starts the connection and the receive loop. Replays from the stored cursor.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _client.SubscribeAsync(WireStreams.Gc(_settings.GcId), cancellationToken);
        _run = RunAsync(_cts.Token);
        _pump = PumpAsync(_cts.Token);
    }

    /// <summary>Sends a message to the group chat and marks it as this client's own.</summary>
    /// <exception cref="WireRequestException">The server refused the message.</exception>
    public async Task SendAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var sent = await _client.RequestAsync<GcSend, GcSendResult>(
            WireTypes.GcSend, new GcSend(_settings.GcId, text, null, null), cancellationToken);
        Timeline.MarkOwn(sent.N);
        Raise();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        // Cancelling twice is harmless; the source itself is never disposed, so a second call stays safe.
        await _cts.CancelAsync();
        try
        {
            await Task.WhenAll(_run, _pump).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        Status = "connected";
        Raise();
        try
        {
            await _client.RunAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }

        Status = _client.Stopped is null or WireClientStopReason.Cancelled ? "closed" : $"stopped: {_client.Stopped}";
        Raise();
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (WireGcMessage message in _client.Messages.ReadAllAsync(cancellationToken))
            {
                if (message.GcId != _settings.GcId) continue;
                if (Timeline.Apply(message)) Raise();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Raise() => Changed?.Invoke(this, EventArgs.Empty);
}
