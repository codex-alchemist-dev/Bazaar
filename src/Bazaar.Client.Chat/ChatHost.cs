using Avalonia.Controls;
using Bazaar.Client.Abstractions.Wire;
using Bazaar.Contracts.Wire;

namespace Bazaar.Client.Chat;

/// <summary>
/// One running chat for a desktop head: the session, its view model and its view. The head asks for the view, puts it in
/// its main window, and disposes the host when the window closes. This keeps the start, error-logging and disposal
/// order in one place, so the Windows and Linux heads do not each carry a copy.
/// </summary>
public sealed class ChatHost : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private int _disposed;

    private ChatHost(GcChatSession session, Action<Action>? dispatch, Action<string> log)
    {
        Session = session;
        Model = new ChatViewModel(session, dispatch);
        View = ChatViewFactory.Instance.Create(Model);
        Started = StartLoggingAsync(token => session.StartAsync(token), log, _stop.Token);
    }

    /// <summary>The chat session.</summary>
    public GcChatSession Session { get; }

    /// <summary>The view model the view is bound to.</summary>
    public ChatViewModel Model { get; }

    /// <summary>The chat view, ready to be placed in a window.</summary>
    public Control View { get; }

    /// <summary>Completes when the session has started, or when its start failed and the failure was logged. It never faults.</summary>
    public Task Started { get; }

    /// <summary>Builds the session from the settings and starts it.</summary>
    /// <param name="settings">Server, token and group chat.</param>
    /// <param name="cursors">Where replay cursors are kept.</param>
    /// <param name="deviceClass">Device class the server uses to pick a UI profile, such as <c>linux</c>.</param>
    /// <param name="clientBuild">Build label sent in <c>hello</c>.</param>
    /// <param name="dispatch">Runs an action on the UI thread. Null runs it on the calling thread.</param>
    /// <param name="log">Receives one line when the session fails to start.</param>
    /// <param name="onWelcome">Optional; called with each server <c>welcome</c>.</param>
    public static ChatHost Start(
        ChatConnectionSettings settings,
        ICursorStore cursors,
        string deviceClass,
        string clientBuild,
        Action<Action>? dispatch,
        Action<string> log,
        Action<Welcome>? onWelcome = null) =>
        Start(ChatConnection.Create(settings, cursors, deviceClass, clientBuild, onWelcome), dispatch, log);

    /// <summary>Starts a host on a session the caller built.</summary>
    public static ChatHost Start(GcChatSession session, Action<Action>? dispatch, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(log);
        return new ChatHost(session, dispatch, log);
    }

    /// <summary>
    /// Runs <paramref name="start"/> and logs one line if it fails. A cancellation is not a failure. The returned task never faults.
    /// </summary>
    public static async Task StartLoggingAsync(Func<CancellationToken, Task> start, Action<string> log, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(log);
        try
        {
            await start(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The host was disposed while the session was starting. That is a stop, not a failure.
        }
        catch (Exception ex)
        {
            log($"Bazaar: chat failed to start: {ex.Message}");
        }
    }

    /// <summary>Stops the session, detaches the view model and disposes the session. Safe to call more than once.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        Model.Dispose();
        await Session.DisposeAsync().ConfigureAwait(false);
        _stop.Dispose();
    }
}
