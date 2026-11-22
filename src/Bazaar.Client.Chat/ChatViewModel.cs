using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Bazaar.Client.Abstractions.Wire;
using Bazaar.Client.Chat.Abstractions;

namespace Bazaar.Client.Chat;

/// <summary>
/// State behind the chat view: the lines, the draft and the status. Pure apart from the injected dispatcher, so
/// tests run it without a window.
/// </summary>
public sealed class ChatViewModel : IChatViewModel
{
    private readonly GcChatSession _session;
    private readonly Action<Action> _dispatch;
    private string _draft = string.Empty;
    private string _status = string.Empty;
    private string? _error;
    private readonly IChatReactionSource? _reactions;
    private readonly IChatTypingSource? _typing;
    private readonly Dictionary<long, ReactionList> _reactionLists = [];
    private string _typingLabel = string.Empty;

    /// <param name="session">The group chat to show.</param>
    /// <param name="dispatch">Runs an action on the UI thread. Defaults to running it inline.</param>
    /// <param name="reactions">Where reaction chips come from, or null for none.</param>
    /// <param name="typing">Who is typing, or null for nobody.</param>
    public ChatViewModel(GcChatSession session, Action<Action>? dispatch = null, IChatReactionSource? reactions = null, IChatTypingSource? typing = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _dispatch = dispatch ?? (action => action());
        _reactions = reactions;
        _typing = typing;
        _session.Changed += OnSessionChanged;
        if (_reactions is not null) _reactions.Changed += OnReactionsChanged;
        if (_typing is not null) _typing.Changed += OnTypingChanged;
        UpdateTyping();
        Refresh();
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The lines, oldest first.</summary>
    public ObservableCollection<ChatLine> Lines { get; } = [];

    /// <summary>The text being typed.</summary>
    public string Draft
    {
        get => _draft;
        set
        {
            if (_draft == value) return;
            SetProperty(ref _draft, value);

            // Typing is a hint to the other members: a non-empty draft is a start or refresh (the session throttles it), an empty one a stop.
            _ = _session.NotifyTypingAsync(value.Trim().Length > 0);
        }
    }

    /// <summary>The session status line.</summary>
    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    /// <summary>The typing line, such as <c>Charles is typing</c>, or empty when nobody types.</summary>
    public string TypingLabel
    {
        get => _typingLabel;
        private set
        {
            if (_typingLabel == value) return;
            SetProperty(ref _typingLabel, value);
            OnPropertyChanged(nameof(IsTyping));
        }
    }

    /// <summary>True while someone is typing, so the view shows the typing dots.</summary>
    public bool IsTyping => _typingLabel.Length > 0;

    /// <summary>The last send error, or null.</summary>
    public string? Error
    {
        get => _error;
        private set => SetProperty(ref _error, value);
    }

    /// <summary>Sends the draft. A refused send keeps the text so nothing is lost.</summary>
    public async Task SendDraftAsync(CancellationToken cancellationToken = default)
    {
        string text = Draft.Trim();
        if (text.Length == 0) return;

        Draft = string.Empty;
        try
        {
            await _session.SendAsync(text, cancellationToken);
            Error = null;
        }
        catch (WireRequestException ex)
        {
            Draft = text;
            Error = $"Send failed: {ex.Error.Code}";
        }
    }

    /// <summary>Rebuilds the lines and status from the session. Call on the UI thread.</summary>
    public void Refresh()
    {
        var lines = _session.Timeline.Lines();
        if (_reactions is not null) lines = [.. lines.Select(l => l with { Reactions = ListFor(l.N) })];
        Merge(lines);
        Status = _session.Status;
    }

    // Keeps the collection stable across refreshes: a line already shown stays (or is replaced when edited) and only new lines are
    // added, so the view can tell a live arrival from a reload and animate only the arrival. Any other shape rebuilds the collection.
    private void Merge(IReadOnlyList<ChatLine> lines)
    {
        int shared = Math.Min(Lines.Count, lines.Count);
        for (int i = 0; i < shared; i++)
        {
            if (Lines[i].N != lines[i].N)
            {
                Lines.Clear();
                foreach (ChatLine line in lines) Lines.Add(line);
                return;
            }
        }

        if (lines.Count < Lines.Count)
        {
            Lines.Clear();
            foreach (ChatLine line in lines) Lines.Add(line);
            return;
        }

        for (int i = 0; i < shared; i++)
        {
            if (Lines[i] != lines[i]) Lines[i] = lines[i];
        }

        for (int i = shared; i < lines.Count; i++) Lines.Add(lines[i]);
    }

    // One stable list per message, synced from the source, so a chip that joins or leaves is a collection edit the view animates.
    private ReactionList ListFor(long n)
    {
        if (_reactionLists.TryGetValue(n, out ReactionList? list)) return list;
        list = new ReactionList();
        list.SyncTo(_reactions!.Reactions(n));
        _reactionLists[n] = list;
        return list;
    }

    private void OnReactionsChanged(object? sender, long n) =>
        _dispatch(() =>
        {
            if (_reactionLists.TryGetValue(n, out ReactionList? list)) list.SyncTo(_reactions!.Reactions(n));
        });

    private void OnTypingChanged(object? sender, EventArgs e) => _dispatch(UpdateTyping);

    private void UpdateTyping()
    {
        var names = _typing?.Typers ?? [];
        TypingLabel = names.Count switch
        {
            0 => string.Empty,
            1 => $"{names[0]} is typing",
            2 => $"{names[0]} and {names[1]} are typing",
            _ => "Several people are typing",
        };
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _session.Changed -= OnSessionChanged;
        if (_reactions is not null) _reactions.Changed -= OnReactionsChanged;
        if (_typing is not null) _typing.Changed -= OnTypingChanged;
    }

    private void OnSessionChanged(object? sender, EventArgs e) => _dispatch(Refresh);

    private void SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(name);
    }

    private void OnPropertyChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
