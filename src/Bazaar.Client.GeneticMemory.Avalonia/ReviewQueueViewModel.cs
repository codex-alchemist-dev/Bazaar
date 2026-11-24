using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;

namespace Bazaar.Client.GeneticMemory.Avalonia;

/// <summary>One row of the queue as the panel shows it.</summary>
/// <param name="Item">The staged candidate.</param>
public sealed record ReviewRow(GmtQueueItem Item)
{
    /// <summary>The title.</summary>
    public string Title => Item.Title;

    /// <summary>Who proposed it and who would receive it.</summary>
    public string Route => $"{Item.ChildAgent} to {Item.TargetAgent}";

    /// <summary>Class and kind, for example <c>Lesson, Procedure</c>.</summary>
    public string Label => $"{Item.ContentClass}, {Item.Kind}";

    /// <summary>Warnings the reviewer should see before deciding.</summary>
    public string Flags => string.Join("  ", new[] { Item.Tainted ? "Tainted: stays T1" : null, Item.OwnerOnly ? "Owner only" : null }.Where(f => f is not null));

    /// <summary>When it was staged.</summary>
    public string StagedText => Item.StagedAt.ToString("u", CultureInfo.InvariantCulture);
}

/// <summary>
/// The Genetic Memory Transfer review panel (doc 25: "Review queue: candidate, trace link, accept, edit or reject"). It lists staged
/// candidates, lets the reviewer edit the title and body, and sends accept or reject to the server. The candidate text is shown as quoted
/// data. The client decides nothing: the server checks the reviewer, screens an edit again and writes the note.
/// </summary>
public sealed class ReviewQueueViewModel : INotifyPropertyChanged
{
    private readonly IGmtQueueClient _client;
    private readonly GmtReviewerRole _role;
    private ReviewRow? _selected;
    private string _title = "";
    private string _body = "";
    private string _notice = "";
    private bool _isBusy;

    /// <summary>Creates the panel for one reviewer role.</summary>
    public ReviewQueueViewModel(IGmtQueueClient client, GmtReviewerRole role)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _role = role;
        AcceptCommand = new ReviewCommand(() => _ = AcceptAsync(), () => CanDecide);
        RejectCommand = new ReviewCommand(() => _ = RejectAsync(), () => CanDecide);
        RefreshCommand = new ReviewCommand(() => _ = RefreshAsync(), () => !_isBusy);
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Staged candidates, oldest first.</summary>
    public ObservableCollection<ReviewRow> Rows { get; } = [];

    /// <summary>Accepts the selected candidate with the edited text.</summary>
    public ReviewCommand AcceptCommand { get; }

    /// <summary>Rejects the selected candidate.</summary>
    public ReviewCommand RejectCommand { get; }

    /// <summary>Reloads the queue.</summary>
    public ReviewCommand RefreshCommand { get; }

    /// <summary>The selected row, or null. Selecting loads its text into the edit fields.</summary>
    public ReviewRow? Selected
    {
        get => _selected;
        set
        {
            if (ReferenceEquals(_selected, value))
            {
                return;
            }

            _selected = value;
            _title = value?.Item.Title ?? "";
            _body = value?.Item.Body ?? "";
            RaiseAll();
        }
    }

    /// <summary>The title the reviewer will accept. Starts as the staged title.</summary>
    public string EditedTitle
    {
        get => _title;
        set => SetText(ref _title, value, nameof(EditedTitle));
    }

    /// <summary>The body the reviewer will accept. Starts as the staged body.</summary>
    public string EditedBody
    {
        get => _body;
        set => SetText(ref _body, value, nameof(EditedBody));
    }

    /// <summary>The link to the child's trace, or empty.</summary>
    public string TraceText => _selected?.Item.TraceRef is { } trace ? "Trace: " + trace : "";

    /// <summary>True when the queue has no candidates.</summary>
    public bool IsEmpty => Rows.Count == 0;

    /// <summary>Feedback for the last action, or empty.</summary>
    public string Notice
    {
        get => _notice;
        private set
        {
            _notice = value;
            Raise(nameof(Notice));
            Raise(nameof(HasNotice));
        }
    }

    /// <summary>True when there is a notice.</summary>
    public bool HasNotice => _notice.Length > 0;

    /// <summary>True while a call is in progress; the buttons are off.</summary>
    public bool IsBusy => _isBusy;

    /// <summary>True when a row is selected and nothing is in progress.</summary>
    public bool CanDecide => !_isBusy && _selected is not null;

    /// <summary>Loads the queue from the server and keeps the selection when the candidate is still staged.</summary>
    public async Task RefreshAsync()
    {
        await RunAsync(async () =>
        {
            var items = await _client.ListAsync(_role, CancellationToken.None);
            var keep = _selected?.Item.Id;
            Rows.Clear();
            foreach (var item in items)
            {
                Rows.Add(new ReviewRow(item));
            }

            Selected = Rows.FirstOrDefault(r => r.Item.Id == keep);
            Notice = "";
        });
    }

    /// <summary>Accepts the selected candidate. Only the fields the reviewer changed are sent as edits.</summary>
    public async Task AcceptAsync()
    {
        if (_selected is not { } row)
        {
            return;
        }

        var title = string.Equals(_title, row.Item.Title, StringComparison.Ordinal) ? null : _title;
        var body = string.Equals(_body, row.Item.Body, StringComparison.Ordinal) ? null : _body;
        await DecideAsync(row, () => _client.AcceptAsync(row.Item.Id, _role, title, body, CancellationToken.None), "Accepted.");
    }

    /// <summary>Rejects the selected candidate.</summary>
    public async Task RejectAsync()
    {
        if (_selected is not { } row)
        {
            return;
        }

        await DecideAsync(row, () => _client.RejectAsync(row.Item.Id, _role, CancellationToken.None), "Rejected.");
    }

    private async Task DecideAsync(ReviewRow row, Func<Task<GmtDecisionResult>> call, string done)
    {
        if (!CanDecide)
        {
            return;
        }

        await RunAsync(async () =>
        {
            var result = await call();
            if (result.Succeeded)
            {
                Rows.Remove(row);
                Selected = null;
                Notice = done;
            }
            else
            {
                // The candidate stays in the queue so the reviewer can fix the edit and try again.
                Notice = result.Message ?? "The server refused this. Nothing changed.";
            }
        });
    }

    private async Task RunAsync(Func<Task> work)
    {
        _isBusy = true;
        RaiseAll();
        try
        {
            await work();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Notice = "Could not reach the server. Nothing changed.";
        }
        finally
        {
            _isBusy = false;
            RaiseAll();
        }
    }

    private void SetText(ref string field, string value, string name)
    {
        value ??= "";
        if (string.Equals(field, value, StringComparison.Ordinal))
        {
            return;
        }

        field = value;
        Raise(name);
    }

    private void RaiseAll()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        AcceptCommand.RaiseCanExecuteChanged();
        RejectCommand.RaiseCanExecuteChanged();
        RefreshCommand.RaiseCanExecuteChanged();
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
