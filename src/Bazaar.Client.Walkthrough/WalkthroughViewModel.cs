using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace Bazaar.Client.Walkthrough;

/// <summary>
/// The first-run walkthrough shown after sign-in (doc 45 section 2.7): welcome, create the company, meet the first agent, give a
/// first task. Every step is skippable, nothing advances by itself, and the person's own data is used (no sample company).
/// A step shows as done when the server says its real action happened; the person still moves on with Next.
/// </summary>
public sealed class WalkthroughViewModel : INotifyPropertyChanged
{
    private readonly IWalkthroughProgressStore _store;
    private readonly IWalkthroughFactsSource _facts;
    private readonly IWalkthroughActions _actions;
    private readonly ClientCommand _next;
    private readonly ClientCommand _back;
    private readonly ClientCommand _skip;
    private readonly ClientCommand _act;
    private readonly ClientCommand _refresh;
    private readonly HashSet<WalkthroughStep> _completed = [];
    private WalkthroughFacts _known = WalkthroughFacts.None;
    private WalkthroughStep _step = WalkthroughStep.Welcome;
    private bool _visible;
    private bool _finished;
    private string _status = "";

    public WalkthroughViewModel(IWalkthroughProgressStore store, IWalkthroughFactsSource facts, IWalkthroughActions actions)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _facts = facts ?? throw new ArgumentNullException(nameof(facts));
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
        _next = new ClientCommand(NextAsync, () => Visible && _step != WalkthroughStep.Done);
        _back = new ClientCommand(BackAsync, () => Visible && _step != WalkthroughStep.Welcome);
        _skip = new ClientCommand(SkipAsync, () => Visible);
        _act = new ClientCommand(ActAsync, () => Visible && WalkthroughText.ActionLabel(_step) is not null);
        _refresh = new ClientCommand(RefreshAsync, () => Visible);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>True while the walkthrough is on screen.</summary>
    public bool Visible
    {
        get => _visible;
        private set { if (Set(ref _visible, value)) ChangeCommands(); }
    }

    public WalkthroughStep Step
    {
        get => _step;
        private set
        {
            if (!Set(ref _step, value)) return;
            Raise(nameof(Title));
            Raise(nameof(Body));
            Raise(nameof(ProgressText));
            Raise(nameof(ActionLabel));
            Raise(nameof(IsStepDone));
            ChangeCommands();
        }
    }

    public string Title => WalkthroughText.Title(_step);

    public string Body => WalkthroughText.Body(_step, _known);

    public string ProgressText => WalkthroughText.Progress(_step);

    public string? ActionLabel => WalkthroughText.ActionLabel(_step);

    /// <summary>True when the server reports this step's real action as done.</summary>
    public bool IsStepDone => _completed.Contains(_step);

    /// <summary>A short line about the last action, for a screen reader's live region. Never repeats a secret.</summary>
    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public ICommand NextCommand => _next;
    public ICommand BackCommand => _back;
    public ICommand SkipCommand => _skip;
    public ICommand ActionCommand => _act;
    public ICommand RefreshCommand => _refresh;

    /// <summary>
    /// Loads saved progress and the server's facts, and decides whether to show. A person who skipped or finished sees nothing;
    /// a person whose company already exists starts at the first step that is not done.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var saved = await LoadProgressAsync(cancellationToken).ConfigureAwait(false);
        _finished = saved.Finished;
        foreach (var done in saved.Completed) _completed.Add(done);
        if (saved.Dismissed || saved.Finished)
        {
            Visible = false;
            return;
        }

        await ReadFactsAsync(cancellationToken).ConfigureAwait(false);
        Step = _completed.Contains(saved.Current) || saved.Current == WalkthroughStep.Welcome
            ? FirstOpenStep(saved.Current)
            : saved.Current;
        Visible = true;
    }

    /// <summary>Shows the walkthrough again from the start (the palette's <c>tour</c>-style command). Settings are never touched.</summary>
    public async Task ReplayAsync(CancellationToken cancellationToken = default)
    {
        _completed.Clear();
        _finished = false;
        await ReadFactsAsync(cancellationToken).ConfigureAwait(false);
        Step = WalkthroughStep.Welcome;
        Visible = true;
        Status = "Walkthrough restarted.";
        await SaveAsync(dismissed: false, cancellationToken).ConfigureAwait(false);
    }

    private WalkthroughStep FirstOpenStep(WalkthroughStep from)
    {
        // Welcome is always shown to a person who has not seen it; later steps that are already done are passed over.
        var step = from;
        while (step < WalkthroughStep.FirstTask && _completed.Contains(step) && step != WalkthroughStep.Welcome) step++;
        return step;
    }

    private async Task NextAsync()
    {
        if (_step == WalkthroughStep.Done) return;
        var next = _step + 1;
        if (next == WalkthroughStep.Done) _finished = true;
        Step = next;
        await SaveAsync(dismissed: false, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task BackAsync()
    {
        if (_step == WalkthroughStep.Welcome) return;
        _finished = false;
        Step = _step - 1;
        await SaveAsync(dismissed: false, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task SkipAsync()
    {
        Visible = false;
        Status = "Walkthrough hidden. Run it again from the command palette.";
        await SaveAsync(dismissed: true, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task ActAsync()
    {
        bool opened;
        try
        {
            opened = await _actions.OpenAsync(_step, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            opened = false;
        }

        Status = opened ? "Opened." : "That screen could not be opened. Try again, or use the command palette.";
    }

    private async Task RefreshAsync()
    {
        await ReadFactsAsync(CancellationToken.None).ConfigureAwait(false);
        await SaveAsync(dismissed: false, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task ReadFactsAsync(CancellationToken cancellationToken)
    {
        WalkthroughFacts facts;
        try
        {
            facts = await _facts.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Keep what was last known; the walkthrough works without the server's answer.
            Status = "Could not check your progress. You can still go through the steps.";
            return;
        }

        _known = facts;
        if (facts.CompanyCreated) _completed.Add(WalkthroughStep.CreateCompany);
        if (facts.FirstAgentName is { Length: > 0 }) _completed.Add(WalkthroughStep.MeetAgent);
        if (facts.FirstTaskGiven) _completed.Add(WalkthroughStep.FirstTask);
        Raise(nameof(Body));
        Raise(nameof(IsStepDone));
    }

    private async Task<WalkthroughProgress> LoadProgressAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _store.LoadAsync(cancellationToken).ConfigureAwait(false) ?? WalkthroughProgress.Fresh;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // An unreadable store must not stop the app: start fresh.
            return WalkthroughProgress.Fresh;
        }
    }

    private async Task SaveAsync(bool dismissed, CancellationToken cancellationToken)
    {
        try
        {
            await _store.SaveAsync(new WalkthroughProgress(dismissed, _finished, _step, [.. _completed.Order()]), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Progress is a convenience; losing it only means the walkthrough may show again.
        }
    }

    private void ChangeCommands()
    {
        _next.Changed();
        _back.Changed();
        _skip.Changed();
        _act.Changed();
        _refresh.Changed();
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    private void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
