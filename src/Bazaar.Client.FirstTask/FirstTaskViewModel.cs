using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace Bazaar.Client.FirstTask;

/// <summary>
/// The first-task composer (doc 45 section 2.10): one box, one Send. Send posts an ordinary group-chat message that mentions the
/// first agent. It is off until the target is known, and a failed send keeps the text so nothing the person wrote is lost.
/// </summary>
public sealed class FirstTaskViewModel : INotifyPropertyChanged
{
    /// <summary>The longest task the box takes, so one paste cannot become a huge message.</summary>
    public const int MaxLength = 4000;

    private readonly IFirstTaskSender _sender;
    private readonly IFirstTaskTargetSource _targets;
    private readonly ClientCommand _send;
    private FirstTaskTarget? _target;
    private string _text = "";
    private string _message = "Checking that your first agent is ready.";
    private bool _sent;

    public FirstTaskViewModel(IFirstTaskSender sender, IFirstTaskTargetSource targets)
    {
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _targets = targets ?? throw new ArgumentNullException(nameof(targets));
        _send = new ClientCommand(SendAsync, () => _target is not null && !_sent && Clean(_text).Length > 0);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Text
    {
        get => _text;
        set
        {
            var capped = value.Length > MaxLength ? value[..MaxLength] : value;
            if (_text == capped) return;
            _text = capped;
            Raise();
            _send.Changed();
        }
    }

    /// <summary>One line for the person and a screen reader's live region. Never repeats the task text.</summary>
    public string Message
    {
        get => _message;
        private set
        {
            if (_message == value) return;
            _message = value;
            Raise();
        }
    }

    /// <summary>True once the task was posted. The box then reads as done.</summary>
    public bool Sent
    {
        get => _sent;
        private set
        {
            if (_sent == value) return;
            _sent = value;
            Raise();
            _send.Changed();
        }
    }

    public ICommand SendCommand => _send;

    /// <summary>Reads the target. Call again to re-check after the company or its agent appears.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _target = await _targets.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _target = null;
        }

        Message = _target is null
            ? "Your first agent is not ready yet. Create the company first, then come back."
            : "Ask your agent for something small. They reply in the group chat.";
        _send.Changed();
    }

    private async Task SendAsync()
    {
        if (_target is null) return;
        var outcome = await _sender.SendAsync(_target, Clean(_text), CancellationToken.None).ConfigureAwait(false);
        if (outcome == FirstTaskOutcome.Sent)
        {
            Sent = true;
            Message = "Sent. Your agent will answer in the group chat.";
        }
        else
        {
            Message = "That did not send. Your text is still here. Try again in a moment.";
        }
    }

    private static string Clean(string text) => text.Trim();

    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
