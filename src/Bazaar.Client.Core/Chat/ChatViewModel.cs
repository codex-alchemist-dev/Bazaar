using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Bazaar.Client.Core.Wire;

namespace Bazaar.Client.Core.Chat;

/// <summary>
/// State behind the chat view: the lines, the draft and the status. Pure apart from the injected dispatcher, so
/// tests run it without a window.
/// </summary>
public sealed class ChatViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly GcChatSession _session;
    private readonly Action<Action> _dispatch;
    private string _draft = string.Empty;
    private string _status = string.Empty;
    private string? _error;

    /// <param name="session">The group chat to show.</param>
    /// <param name="dispatch">Runs an action on the UI thread. Defaults to running it inline.</param>
    public ChatViewModel(GcChatSession session, Action<Action>? dispatch = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _dispatch = dispatch ?? (action => action());
        _session.Changed += OnSessionChanged;
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
        set => SetProperty(ref _draft, value);
    }

    /// <summary>The session status line.</summary>
    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

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
        Lines.Clear();
        foreach (ChatLine line in lines) Lines.Add(line);
        Status = _session.Status;
    }

    /// <inheritdoc />
    public void Dispose() => _session.Changed -= OnSessionChanged;

    private void OnSessionChanged(object? sender, EventArgs e) => _dispatch(Refresh);

    private void SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
