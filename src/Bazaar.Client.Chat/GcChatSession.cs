using Bazaar.Client.Abstractions.Wire;
using Bazaar.Contracts.Wire;

namespace Bazaar.Client.Chat;

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
    private readonly Lock _typingGate = new();
    private DateTimeOffset _typingSentAt = DateTimeOffset.MinValue;
    private bool _typingActive;

    /// <param name="client">The wire client that carries the group chat's stream.</param>
    /// <param name="settings">Which group chat to show.</param>
    /// <param name="timeline">Holds the messages; the session fills it.</param>
    public GcChatSession(WireClient client, ChatConnectionSettings settings, ChatTimeline timeline)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        Timeline = timeline ?? throw new ArgumentNullException(nameof(timeline));
        Reactions = new WireChatReactionSource(settings.GcId, () => _client.PrincipalId);
        Typing = new WireChatTypingSource(settings.GcId, Timeline.NameOf);
    }

    /// <summary>
    /// How often this client refreshes its own typing signal while the person types. Half the server's default lifetime for a
    /// signal (<c>wire.typing_ttl_s</c>, 5 s), so one lost refresh does not drop the indicator.
    /// </summary>
    public static readonly TimeSpan TypingRefresh = TimeSpan.FromMilliseconds(2500);

    /// <summary>Reactions on the messages of this group chat, from the wire.</summary>
    public WireChatReactionSource Reactions { get; }

    /// <summary>Who is typing in this group chat, from the wire.</summary>
    public WireChatTypingSource Typing { get; }

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
        _client.ReactionReceived += OnReaction;
        _client.TypingReceived += OnTyping;
        _client.Connected += OnConnected;
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

    /// <summary>
    /// Adds or removes this client's reaction on a message. The local state follows at once and the server's live event repeats it
    /// harmlessly.
    /// </summary>
    /// <exception cref="WireRequestException">The server refused the reaction (not a member, a limit, a bad emoji).</exception>
    public async Task ReactAsync(long n, string emoji, bool add, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(emoji);
        var result = await _client.RequestAsync<GcReact, GcReactResult>(
            WireTypes.GcReact, new GcReact(_settings.GcId, n, emoji, add), cancellationToken);
        if (result.Changed && _client.PrincipalId is { } self)
            Reactions.Apply(new WireGcReaction(_settings.GcId, n, emoji, self, add, DateTimeOffset.UtcNow));
    }

    /// <summary>Adds this client's reaction when it is not there, removes it when it is.</summary>
    /// <exception cref="WireRequestException">The server refused the reaction.</exception>
    public Task ToggleReactionAsync(long n, string emoji, CancellationToken cancellationToken = default) =>
        ReactAsync(n, emoji, !Reactions.Reactions(n).Any(r => r.Emoji == emoji && r.Mine), cancellationToken);

    /// <summary>
    /// Tells the server this client's person is typing (<paramref name="typing"/> true) or stopped. Refreshes are sent at most every
    /// <see cref="TypingRefresh"/>; a stop is sent once. Never throws for a refused or lost signal: the indicator is only a hint.
    /// </summary>
    public async Task NotifyTypingAsync(bool typing, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_typingGate)
        {
            if (typing && _typingActive && now - _typingSentAt < TypingRefresh) return;
            if (!typing && !_typingActive) return;
            _typingActive = typing;
            _typingSentAt = now;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            await _client.RequestAsync<GcTyping, GcTypingResult>(WireTypes.GcTyping, new GcTyping(_settings.GcId, typing), timeout.Token);
        }
        catch (Exception ex) when (ex is WireRequestException or OperationCanceledException)
        {
            // A refused, lost or late signal costs a few seconds of indicator and nothing else.
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _client.ReactionReceived -= OnReaction;
        _client.TypingReceived -= OnTyping;
        _client.Connected -= OnConnected;
        Typing.Dispose();
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
                Typing.Clear(message.AuthorId);
                if (Timeline.Apply(message)) Raise();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void OnReaction(WireGcReaction reaction) => Reactions.Apply(reaction);

    private void OnTyping(PresenceTyping signal) => Typing.Apply(signal);

    // Reactions are not replayed with the stream, so every connection fetches the folded state. The receive loop answers requests,
    // so the fetch runs off it.
    private void OnConnected() => _ = Task.Run(() => RefreshReactionsAsync(_cts.Token));

    private async Task RefreshReactionsAsync(CancellationToken cancellationToken)
    {
        Reactions.BeginLoad();
        try
        {
            // Events are held while the snapshot loads, so a server that never answers must not hold them for good.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var reply = await _client.RequestAsync<GcReactionsRequest, GcReactionsReply>(
                WireTypes.GcReactions, new GcReactionsRequest(_settings.GcId, null), timeout.Token);
            Reactions.Load(reply);
        }
        catch (Exception ex) when (ex is WireRequestException or OperationCanceledException)
        {
            Reactions.AbortLoad();
        }
    }

    private void Raise() => Changed?.Invoke(this, EventArgs.Empty);
}
