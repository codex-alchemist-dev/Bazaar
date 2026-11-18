using System.Collections.ObjectModel;
using System.ComponentModel;

namespace Bazaar.Client.Chat.Abstractions;

/// <summary>
/// The group chat state a chat view binds to: the lines, the draft, the status and the last send error. A feature
/// module (the GC pane) takes this contract from the head and never references the chat implementation.
/// </summary>
public interface IChatViewModel : INotifyPropertyChanged, IDisposable
{
    /// <summary>The lines, oldest first.</summary>
    ObservableCollection<ChatLine> Lines { get; }

    /// <summary>The text being typed.</summary>
    string Draft { get; set; }

    /// <summary>The session status line.</summary>
    string Status { get; }

    /// <summary>Sends the draft. A refused send keeps the text so nothing is lost.</summary>
    Task SendDraftAsync(CancellationToken cancellationToken = default);

    /// <summary>Rebuilds the lines and status from the session. Call on the UI thread.</summary>
    void Refresh();
}
