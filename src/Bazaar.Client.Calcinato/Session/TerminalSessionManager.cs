using Bazaar.Client.Calcinato.Abstractions;
using Bazaar.Client.Calcinato.Modal;

namespace Bazaar.Client.Calcinato.Session;

/// <summary>How the visible sessions are split. <c>Horizontal</c> stacks them (<c>:split</c>); <c>Vertical</c> places them side by side (<c>:vsplit</c>).</summary>
public enum TerminalOrientation
{
    /// <summary>Panes stacked top to bottom.</summary>
    Horizontal,

    /// <summary>Panes side by side.</summary>
    Vertical,
}

/// <summary>The visible layout: the session ids in display order, and how they are split.</summary>
/// <param name="Orientation">The split direction for every pane.</param>
/// <param name="SessionIds">Session ids in display order. Every open session is visible.</param>
public sealed record TerminalLayout(TerminalOrientation Orientation, IReadOnlyList<string> SessionIds);

/// <summary>Raised when closing a session needs the person's confirmation.</summary>
/// <param name="SessionId">The session the close applies to.</param>
/// <param name="HasRunningProcess">True for a local shell that has not exited.</param>
public sealed record TerminalCloseRequest(string SessionId, bool HasRunningProcess);

/// <summary>The outcome of one ex command handled by the manager.</summary>
/// <param name="Handled">False when the manager does not own the command, so the head must route it.</param>
/// <param name="Message">Text for the status line, or null.</param>
/// <param name="NeedsConfirmation">True when a close waits for the person's answer.</param>
public sealed record TerminalCommandResult(bool Handled, string? Message = null, bool NeedsConfirmation = false);

/// <summary>The outcome of <see cref="TerminalSessionManager.CloseAsync"/>.</summary>
public enum TerminalCloseOutcome
{
    /// <summary>The session was closed and removed.</summary>
    Closed,

    /// <summary>No open session has that id.</summary>
    NotFound,

    /// <summary>A local shell is running and the close was not forced. <see cref="TerminalSessionManager.CloseRequested"/> was raised.</summary>
    NeedsConfirmation,
}

/// <summary>
/// Owns the open terminal sessions, the active one and the split layout (doc 55, section 5.3). It opens local shells
/// through <see cref="ITerminalHost.OpenLocal"/> and server terminals through <see cref="ITerminalHost.AttachRemoteAsync"/>,
/// and carries out the ex commands the sessions raise. History is never persisted.
/// </summary>
/// <remarks>
/// Commands the manager does not own (<c>:take</c>, <c>:set</c>, <c>:crash</c> and the like) come back as
/// <see cref="TerminalCommandResult.Handled"/> false for the head to route.
/// </remarks>
public sealed class TerminalSessionManager : IAsyncDisposable
{
    private readonly object _syncRoot = new();
    private readonly ITerminalHost _host;
    private readonly TimeProvider _timeProvider;
    private readonly IMnemonicBridge? _mnemonics;
    private readonly int _defaultCols;
    private readonly int _defaultRows;
    private readonly List<TerminalSession> _sessions = [];
    private readonly Queue<ExCommand> _pendingCommands = new();
    private string? _activeId;
    private TerminalOrientation _orientation = TerminalOrientation.Horizontal;
    private int _localCount;
    private string? _message;

    /// <summary>Creates a manager over a head's terminal host.</summary>
    /// <param name="host">Opens local and remote terminals.</param>
    /// <param name="timeProvider">Clock for the emulators. Null uses the system clock.</param>
    /// <param name="mnemonics">Mnemonic router handed to every engine. Null means none.</param>
    /// <param name="defaultCols">Columns for new sessions.</param>
    /// <param name="defaultRows">Rows for new sessions.</param>
    public TerminalSessionManager(
        ITerminalHost host,
        TimeProvider? timeProvider = null,
        IMnemonicBridge? mnemonics = null,
        int defaultCols = 80,
        int defaultRows = 24)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentOutOfRangeException.ThrowIfLessThan(defaultCols, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(defaultRows, 1);

        _host = host;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _mnemonics = mnemonics;
        _defaultCols = defaultCols;
        _defaultRows = defaultRows;
    }

    /// <summary>Raised when sessions, the active session, the layout or the status message change, and when an open session changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised when a close needs the person's confirmation. Confirm with <see cref="CloseAsync"/> and <c>force</c> set.</summary>
    public event EventHandler<TerminalCloseRequest>? CloseRequested;

    /// <summary>Open sessions in display order.</summary>
    public IReadOnlyList<TerminalSession> Sessions
    {
        get
        {
            lock (_syncRoot)
            {
                return _sessions.ToArray();
            }
        }
    }

    /// <summary>The active session, or null when none is open.</summary>
    public TerminalSession? Active
    {
        get
        {
            lock (_syncRoot)
            {
                return _sessions.Find(session => session.Id == _activeId);
            }
        }
    }

    /// <summary>The current layout.</summary>
    public TerminalLayout Layout
    {
        get
        {
            lock (_syncRoot)
            {
                return new TerminalLayout(_orientation, _sessions.Select(session => session.Id).ToArray());
            }
        }
    }

    /// <summary>The last command result text, for the status line. Null when none.</summary>
    public string? Message
    {
        get
        {
            lock (_syncRoot)
            {
                return _message;
            }
        }
    }

    /// <summary>Opens a local shell as a new session and makes it active.</summary>
    /// <param name="shell">Shell path or name. Null picks the default.</param>
    /// <exception cref="NotSupportedException">The head has no local backend.</exception>
    public TerminalSession OpenLocal(string? shell = null)
    {
        if (!_host.SupportsLocal)
        {
            throw new NotSupportedException("This head has no local terminal backend.");
        }

        ITerminalSource source = _host.OpenLocal(new TerminalSpec(shell, _defaultCols, _defaultRows));
        int number;
        lock (_syncRoot)
        {
            number = ++_localCount;
        }

        var session = CreateSession($"local-{number}", source);
        Track(session);
        return session;
    }

    /// <summary>Attaches to a server terminal. An already attached terminal is activated instead of opened twice.</summary>
    /// <param name="terminalId">Server terminal id.</param>
    /// <param name="ct">Cancels the attach.</param>
    public async Task<TerminalSession> AttachAsync(string terminalId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(terminalId);
        string id = RemoteSessionId(terminalId);
        TerminalSession? existing = Find(id);
        if (existing is not null)
        {
            Activate(id);
            return existing;
        }

        ITerminalSource source = await _host.AttachRemoteAsync(terminalId, _defaultCols, _defaultRows, ct).ConfigureAwait(false);
        TerminalSession? raced = Find(id);
        if (raced is not null)
        {
            await source.DisposeAsync().ConfigureAwait(false);
            Activate(id);
            return raced;
        }

        var session = CreateSession(id, source);
        Track(session);
        return session;
    }

    /// <summary>Makes an open session active.</summary>
    /// <param name="id">Session id.</param>
    /// <returns>False when no open session has that id.</returns>
    public bool Activate(string id)
    {
        bool found;
        lock (_syncRoot)
        {
            found = _sessions.Exists(session => session.Id == id);
            if (found)
            {
                _activeId = id;
            }
        }

        if (found)
        {
            RaiseChanged();
        }

        return found;
    }

    /// <summary>
    /// Closes a session. A running local shell is not closed unless <paramref name="force"/> is set: the manager raises
    /// <see cref="CloseRequested"/> and returns <see cref="TerminalCloseOutcome.NeedsConfirmation"/>. Confirm by calling
    /// again with <paramref name="force"/> true (which kills the shell).
    /// </summary>
    /// <param name="id">Session id.</param>
    /// <param name="force">Close without asking, and kill a running local shell.</param>
    public async Task<TerminalCloseOutcome> CloseAsync(string id, bool force)
    {
        TerminalSession? session = Find(id);
        if (session is null)
        {
            return TerminalCloseOutcome.NotFound;
        }

        if (!force && session.IsRunningLocalProcess)
        {
            CloseRequested?.Invoke(this, new TerminalCloseRequest(id, HasRunningProcess: true));
            return TerminalCloseOutcome.NeedsConfirmation;
        }

        lock (_syncRoot)
        {
            int index = _sessions.IndexOf(session);
            _sessions.RemoveAt(index);
            if (_activeId == id)
            {
                _activeId = _sessions.Count == 0 ? null : _sessions[Math.Min(index, _sessions.Count - 1)].Id;
            }
        }

        Unsubscribe(session);
        RaiseChanged();
        await session.DisposeAsync().ConfigureAwait(false);
        return TerminalCloseOutcome.Closed;
    }

    /// <summary>
    /// Carries out one ex command. <c>:new</c>, <c>:split</c> and <c>:vsplit</c> open a local shell (split sets the
    /// orientation first), <c>:attach</c> opens a server terminal, <c>:detach</c> closes a server viewer without
    /// touching the server terminal, and <c>:q</c> and <c>:q!</c> close the active session.
    /// </summary>
    /// <param name="command">The command.</param>
    /// <param name="ct">Cancels attach and close waits.</param>
    public async Task<TerminalCommandResult> ExecuteAsync(ExCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        switch (command.Kind)
        {
            case ExCommandKind.New:
                return Report(OpenLocalResult(command.Argument));

            case ExCommandKind.Split:
                SetOrientation(TerminalOrientation.Horizontal);
                return Report(OpenLocalResult(command.Argument));

            case ExCommandKind.VSplit:
                SetOrientation(TerminalOrientation.Vertical);
                return Report(OpenLocalResult(command.Argument));

            case ExCommandKind.Attach:
                return Report(await AttachFromCommandAsync(command.Argument, ct).ConfigureAwait(false));

            case ExCommandKind.Detach:
                return Report(await DetachActiveAsync().ConfigureAwait(false));

            case ExCommandKind.Quit:
                return Report(await CloseActiveAsync(force: false).ConfigureAwait(false));

            case ExCommandKind.QuitForce:
                return Report(await CloseActiveAsync(force: true).ConfigureAwait(false));

            default:
                return Report(new TerminalCommandResult(false, $"Not handled by the terminal manager: {command.Kind}."));
        }
    }

    /// <summary>
    /// Sends one key press to the active session, then carries out the ex commands its engine raised. With no session
    /// open the key is dropped and the status message says how to open one.
    /// </summary>
    /// <param name="key">The key press.</param>
    /// <param name="ct">Cancels writes and commands.</param>
    public async ValueTask HandleKeyAsync(TerminalKey key, CancellationToken ct = default)
    {
        TerminalSession? active = Active;
        if (active is null)
        {
            Report(new TerminalCommandResult(false, "No terminal is open. Use :new to start a shell."));
            return;
        }

        await active.HandleKeyAsync(key, ct).ConfigureAwait(false);
        while (DequeueCommand() is { } command)
        {
            await ExecuteAsync(command, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Closes every session without asking. Sources are disposed, which ends local shells.</summary>
    public async ValueTask DisposeAsync()
    {
        List<TerminalSession> all;
        lock (_syncRoot)
        {
            all = _sessions.ToList();
            _sessions.Clear();
            _activeId = null;
        }

        foreach (TerminalSession session in all)
        {
            Unsubscribe(session);
            await session.DisposeAsync().ConfigureAwait(false);
        }

        RaiseChanged();
    }

    private static string RemoteSessionId(string terminalId) => $"remote:{terminalId}";

    private TerminalSession CreateSession(string id, ITerminalSource source) =>
        new(id, source, _timeProvider, _defaultCols, _defaultRows, _mnemonics);

    private TerminalSession? Find(string id)
    {
        lock (_syncRoot)
        {
            return _sessions.Find(session => session.Id == id);
        }
    }

    private void Track(TerminalSession session)
    {
        lock (_syncRoot)
        {
            int index = _sessions.FindIndex(existing => existing.Id == _activeId);
            _sessions.Insert(index < 0 ? _sessions.Count : index + 1, session);
            _activeId = session.Id;
        }

        session.Changed += OnSessionChanged;
        session.CommandRequested += OnCommandRequested;
        RaiseChanged();
    }

    private void Unsubscribe(TerminalSession session)
    {
        session.Changed -= OnSessionChanged;
        session.CommandRequested -= OnCommandRequested;
    }

    private void SetOrientation(TerminalOrientation orientation)
    {
        lock (_syncRoot)
        {
            _orientation = orientation;
        }
    }

    private TerminalCommandResult OpenLocalResult(string? shell)
    {
        if (!_host.SupportsLocal)
        {
            return new TerminalCommandResult(true, "This head has no local terminal backend. Use :attach <terminalId> for a server terminal.");
        }

        try
        {
            TerminalSession session = OpenLocal(shell);
            return new TerminalCommandResult(true, $"Opened {session.Id}.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new TerminalCommandResult(true, $"Could not open a shell: {ex.Message}");
        }
    }

    private async Task<TerminalCommandResult> AttachFromCommandAsync(string? terminalId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(terminalId))
        {
            return new TerminalCommandResult(true, "Usage: :attach <terminalId>");
        }

        if (Find(RemoteSessionId(terminalId)) is not null)
        {
            Activate(RemoteSessionId(terminalId));
            return new TerminalCommandResult(true, $"Already attached to {terminalId}.");
        }

        try
        {
            TerminalSession session = await AttachAsync(terminalId, ct).ConfigureAwait(false);
            return new TerminalCommandResult(true, $"Attached to {terminalId} as {session.Id}.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new TerminalCommandResult(true, $"Could not attach to {terminalId}: {ex.Message}");
        }
    }

    private async Task<TerminalCommandResult> DetachActiveAsync()
    {
        TerminalSession? active = Active;
        if (active is null)
        {
            return new TerminalCommandResult(true, "No terminal is open.");
        }

        if (!active.IsRemote)
        {
            return new TerminalCommandResult(true, "A local terminal cannot be detached. Use :q to close it.");
        }

        await CloseAsync(active.Id, force: true).ConfigureAwait(false);
        return new TerminalCommandResult(true, $"Detached from {active.Id}. The server terminal keeps running.");
    }

    private async Task<TerminalCommandResult> CloseActiveAsync(bool force)
    {
        TerminalSession? active = Active;
        if (active is null)
        {
            return new TerminalCommandResult(true, "No terminal is open.");
        }

        TerminalCloseOutcome outcome = await CloseAsync(active.Id, force).ConfigureAwait(false);
        return outcome switch
        {
            TerminalCloseOutcome.Closed => new TerminalCommandResult(true, $"Closed {active.Id}."),
            TerminalCloseOutcome.NeedsConfirmation => new TerminalCommandResult(
                true,
                "The shell is still running. Confirm the close, or use :q! to kill it.",
                NeedsConfirmation: true),
            _ => new TerminalCommandResult(true, "No terminal is open."),
        };
    }

    private TerminalCommandResult Report(TerminalCommandResult result)
    {
        lock (_syncRoot)
        {
            _message = result.Message;
        }

        RaiseChanged();
        return result;
    }

    private ExCommand? DequeueCommand()
    {
        lock (_syncRoot)
        {
            return _pendingCommands.TryDequeue(out ExCommand? command) ? command : null;
        }
    }

    private void OnCommandRequested(object? sender, ExCommand command)
    {
        lock (_syncRoot)
        {
            _pendingCommands.Enqueue(command);
        }
    }

    private void OnSessionChanged(object? sender, EventArgs e) => RaiseChanged();

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
