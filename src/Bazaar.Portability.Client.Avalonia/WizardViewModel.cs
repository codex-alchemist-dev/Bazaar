using System.ComponentModel;
using System.Windows.Input;
using Bazaar.Portability;
using Bazaar.Portability.Client;

namespace Bazaar.Portability.Client.Avalonia;

/// <summary>
/// Bridges the pure <see cref="ImportWizard"/> state machine to the wizard views. It holds the current
/// <see cref="WizardState"/>, runs each transition, and calls the host's <see cref="IImportCommitter"/>
/// for the dry-run and commit stages. The views bind to <see cref="State"/>, the step flags and the commands.
/// </summary>
public sealed class WizardViewModel : INotifyPropertyChanged
{
    private readonly ImportWizard _wizard;
    private readonly IImportCommitter _committer;
    private readonly IReadOnlySet<string> _committedKeys;
    private readonly Func<Task<ImportSource?>> _pickSource;
    private WizardState _state;
    private IReadOnlyList<CandidateChoice> _candidates = [];
    private IReadOnlyList<CollisionChoice> _collisions = [];

    public WizardViewModel(
        ImportWizard wizard,
        IImportCommitter committer,
        IReadOnlySet<string> committedKeys,
        Func<Task<ImportSource?>> pickSource,
        WizardState? initial = null)
    {
        _wizard = wizard ?? throw new ArgumentNullException(nameof(wizard));
        _committer = committer ?? throw new ArgumentNullException(nameof(committer));
        _committedKeys = committedKeys ?? throw new ArgumentNullException(nameof(committedKeys));
        _pickSource = pickSource ?? throw new ArgumentNullException(nameof(pickSource));

        // The async steps run on the UI context; their state changes are raised as PropertyChanged on that thread.
        PickSourceCommand = new WizardCommand(p => { _ = PickSourceAsync(); }, () => State.Step == WizardStep.ChooseSource);
        ChooseImporterCommand = new WizardCommand(id => ChooseImporter(id as string), () => State.Step is WizardStep.Detect or WizardStep.Preview or WizardStep.DryRun);
        DryRunCommand = new WizardCommand(p => { _ = RunDryRunAsync(); }, () => State.Step == WizardStep.Preview);
        CommitCommand = new WizardCommand(p => { _ = RunCommitAsync(); }, () => State.CanCommit);
        RetryCommand = new WizardCommand(p => Retry(), () => State.Step == WizardStep.Failed);

        _state = initial ?? WizardState.Initial;
        Rebuild(_state);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public WizardState State
    {
        get => _state;
        private set
        {
            _state = value;
            Rebuild(value);
            RaiseAll();
        }
    }

    /// <summary>Importers offered by detection. Empty outside the detect step.</summary>
    public IReadOnlyList<CandidateChoice> Candidates => _candidates;

    /// <summary>Collisions in the preview, each with its per-item choice. Empty outside the preview.</summary>
    public IReadOnlyList<CollisionChoice> Collisions => _collisions;

    public bool IsChooseSource => State.Step == WizardStep.ChooseSource;

    public bool IsDetect => State.Step == WizardStep.Detect;

    public bool IsPreview => State.Step == WizardStep.Preview;

    public bool IsDryRun => State.Step == WizardStep.DryRun;

    public bool IsDryRunPending => State.Step == WizardStep.DryRun && State.DryRun is null;

    public bool HasDryRunReport => State.DryRun is not null;

    public bool IsCommit => State.Step == WizardStep.Commit;

    public bool IsDone => State.Step == WizardStep.Done;

    public bool IsFailed => State.Step == WizardStep.Failed;

    public bool HasNotice => State.Notice is not null;

    public bool CanCommit => State.CanCommit;

    public ICommand PickSourceCommand { get; }

    public ICommand ChooseImporterCommand { get; }

    public ICommand DryRunCommand { get; }

    public ICommand CommitCommand { get; }

    public ICommand RetryCommand { get; }

    private async Task PickSourceAsync()
    {
        var source = await _pickSource().ConfigureAwait(true);
        if (source is null)
        {
            return;
        }
        State = _wizard.ChooseSource(State, source, _committedKeys);
    }

    private void ChooseImporter(string? importerId)
    {
        if (string.IsNullOrEmpty(importerId))
        {
            return;
        }
        State = _wizard.ChooseImporter(State, importerId);
    }

    private async Task RunDryRunAsync()
    {
        var plan = State.Plan;
        State = ImportWizard.StartDryRun(State);
        if (plan is null || State.Step != WizardStep.DryRun)
        {
            return;
        }
        var report = await _committer.DryRunAsync(plan, CancellationToken.None).ConfigureAwait(true);
        State = ImportWizard.CompleteDryRun(State, report);
    }

    private async Task RunCommitAsync()
    {
        var plan = State.Plan;
        State = ImportWizard.StartCommit(State);
        if (plan is null || State.Step != WizardStep.Commit)
        {
            return;
        }
        var report = await _committer.CommitAsync(plan, CancellationToken.None).ConfigureAwait(true);
        State = ImportWizard.CompleteCommit(State, report);
    }

    /// <summary>Applies one collision choice from the preview. A refused choice leaves the step as it was.</summary>
    private void ChooseCollision(CollisionChoice choice, CollisionAction action)
    {
        State = ImportWizard.ChooseCollisionAction(State, choice.SourceKey, action);
    }

    private void Retry()
    {
        State = _wizard.Retry(State);
    }

    private void Rebuild(WizardState state)
    {
        _candidates = state.Step == WizardStep.Detect
            ? state.Candidates.Select(c => new CandidateChoice(c, ChooseImporterCommand)).ToList()
            : [];
        _collisions = state.Preview is null ? [] : CollisionRows(state);
    }

    /// <summary>One row per colliding record: incoming collisions, then records that collide with a stored object.</summary>
    private List<CollisionChoice> CollisionRows(WizardState state)
    {
        var rows = new List<CollisionChoice>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var collision in state.Preview!.Collisions)
        {
            foreach (var key in collision.SourceKeys.Where(seen.Add))
            {
                rows.Add(Row(collision.Target, collision.Title, key, state));
            }
        }
        foreach (var existing in state.Preview.ExistingCollisions.Where(e => seen.Add(e.SourceKey)))
        {
            rows.Add(Row(existing.Target, existing.Title, existing.SourceKey, state));
        }
        return rows;
    }

    private CollisionChoice Row(string target, string title, string key, WizardState state) =>
        new(new PreviewCollision(target, title, [key]), key, ImportWizard.ActionFor(state, key), (choice, action) => ChooseCollision(choice, action));

    private void RaiseAll()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        ((WizardCommand)PickSourceCommand).RaiseCanExecuteChanged();
        ((WizardCommand)ChooseImporterCommand).RaiseCanExecuteChanged();
        ((WizardCommand)DryRunCommand).RaiseCanExecuteChanged();
        ((WizardCommand)CommitCommand).RaiseCanExecuteChanged();
    }
}
