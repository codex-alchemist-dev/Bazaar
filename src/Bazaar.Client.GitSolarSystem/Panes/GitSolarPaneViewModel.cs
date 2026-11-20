using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Constellation;
using Bazaar.Client.GitSolarSystem.Abstractions;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Constellation.Rendering.Core;
using Bazaar.Contracts.Graph;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.GitSolarSystem.Panes;

/// <summary>One row of the accessible list mirror: a commit, newest first.</summary>
public sealed record GitSolarListItem(string Sha, string ShortSha, string Subject, string Branch, bool IsComet)
{
    /// <summary>The text the view and screen readers show for the row.</summary>
    public string Text =>
        $"{ShortSha} {(string.IsNullOrWhiteSpace(Subject) ? "(no subject)" : Subject)} · {Branch}" +
        (IsComet ? " · comet (merged branch)" : string.Empty);
}

/// <summary>
/// The Git Solar System pane (pane kind <see cref="PaneKinds.GitSolarSystem"/>, doc 19). Model:
/// <c>{ "repository": "provider/owner/repo" }</c>. A missing or empty repository raises <see cref="PaneModelException"/>.
/// </summary>
/// <remarks>
/// The pane consumes <see cref="IGitSolarSource.UpdatesAsync"/> and applies each update through <see cref="IGitSolarModel"/>.
/// The list mirror and the Up/Down/Home/End stepping are <see cref="AccessibleNodeList"/>: each commit becomes one node, and its
/// kind is a time rank, so the list's kind-then-label order is the newest-first time order. Updates arrive on the feed's
/// thread; <see cref="PropertyChanged"/> is raised on that thread, and the view marshals to the UI thread.
/// </remarks>
public sealed class GitSolarPaneViewModel : IPaneViewModel, INotifyPropertyChanged, IDisposable
{
    private const string _notConnected = "No solar-system feed is connected.";
    private const string _waiting = "Waiting for the repository.";
    private const string _stale = "Stale";
    private const string _live = "Live";

    private readonly object _gate = new();
    private readonly IGitSolarModel _model;
    private readonly bool _connected;
    private readonly CancellationTokenSource? _cancellation;
    private List<GitSolarListItem> _items = [];
    private IReadOnlyList<AccessibleNodeInput> _inputs = [];
    private AccessibleNodeList _list = AccessibleNodeList.Create([], null);
    private bool _hasSnapshot;
    private bool _disposed;

    public GitSolarPaneViewModel(PaneDescriptor descriptor, IGitSolarSource? source)
        : this(descriptor, source, new GitSolarStateModel())
    {
    }

    internal GitSolarPaneViewModel(PaneDescriptor descriptor, IGitSolarSource? source, IGitSolarModel model)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(model);

        Kind = descriptor.Kind;
        Title = "Git · Solar System";
        Repository = ReadRepository(descriptor.Model);
        _model = model;
        _connected = source is not null;

        if (source is not null)
        {
            _cancellation = new CancellationTokenSource();
            _ = ConsumeAsync(source, Repository, _cancellation);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised when <see cref="OpenSelected"/> is called with a commit selected.</summary>
    public event Action<GitSolarOpenCommit>? OpenCommitRequested;

    public string Kind { get; }

    public string Title { get; }

    /// <summary>The repository the pane follows, from the descriptor model.</summary>
    public string Repository { get; }

    /// <summary>The commits, newest first. The same order as <see cref="SelectedIndex"/>.</summary>
    public IReadOnlyList<GitSolarListItem> Items
    {
        get
        {
            lock (_gate)
            {
                return _items;
            }
        }
    }

    /// <summary>Index of the selected row in <see cref="Items"/>, or -1 when nothing is selected.</summary>
    public int SelectedIndex
    {
        get
        {
            lock (_gate)
            {
                return _list.FocusedIndex;
            }
        }
    }

    public string? SelectedSha
    {
        get
        {
            lock (_gate)
            {
                return _model.SelectedSha;
            }
        }
    }

    /// <summary>The renderer-ready scene of the latest accepted snapshot, or null before the first one.</summary>
    public GitSolarScene? Scene
    {
        get
        {
            lock (_gate)
            {
                return _model.Scene;
            }
        }
    }

    /// <summary>The latest accepted layout frame for <see cref="Scene"/>, or null when none applies.</summary>
    public RenderFrame? LatestFrame
    {
        get
        {
            lock (_gate)
            {
                return _model.LatestFrame;
            }
        }
    }

    public bool IsStale
    {
        get
        {
            lock (_gate)
            {
                return _model.IsStale;
            }
        }
    }

    /// <summary>The status line: not connected, waiting, stale or live.</summary>
    public string StatusText
    {
        get
        {
            lock (_gate)
            {
                if (!_connected)
                {
                    return _notConnected;
                }

                if (_model.IsStale)
                {
                    return _stale;
                }

                return _hasSnapshot ? _live : _waiting;
            }
        }
    }

    /// <summary>Moves the selection to the next row, in list order. Wraps, as <see cref="AccessibleNodeList.NextIndex"/> does.</summary>
    public void MoveNext() => MoveTo(() => _list.NextIndex(_list.FocusedIndex));

    /// <summary>Moves the selection to the previous row, in list order. Wraps, as <see cref="AccessibleNodeList.PreviousIndex"/> does.</summary>
    public void MovePrevious() => MoveTo(() => _list.PreviousIndex(_list.FocusedIndex));

    /// <summary>Selects the first (newest) row.</summary>
    public void MoveFirst() => SelectIndex(0);

    /// <summary>Selects the last (oldest) row.</summary>
    public void MoveLast()
    {
        int last;
        lock (_gate)
        {
            last = _items.Count - 1;
        }

        SelectIndex(last);
    }

    /// <summary>Selects the row at <paramref name="index"/>. Out-of-range indexes are ignored.</summary>
    public void SelectIndex(int index) => MoveTo(() => index);

    /// <summary>
    /// Raises <see cref="OpenCommitRequested"/> for the selected commit (<see cref="GitSolarCommands.OpenCommit"/>).
    /// Returns false when nothing is selected.
    /// </summary>
    public bool OpenSelected()
    {
        GitSolarOpenCommit? open;
        lock (_gate)
        {
            open = _model.ToOpenCommit();
        }

        if (open is null)
        {
            return false;
        }

        OpenCommitRequested?.Invoke(open);
        return true;
    }

    /// <summary>
    /// Handles <see cref="GitSolarCommands.FocusCommit"/> (argument: sha) and <see cref="GitSolarCommands.FocusBranch"/>
    /// (argument: branch name). Returns false for an unknown command or an argument the snapshot does not hold.
    /// </summary>
    public bool Handle(string commandId, string argument)
    {
        ArgumentNullException.ThrowIfNull(commandId);
        ArgumentNullException.ThrowIfNull(argument);

        return commandId switch
        {
            GitSolarCommands.FocusCommit => Select(model => model.SelectBySha(argument)),
            GitSolarCommands.FocusBranch => Select(model => model.SelectBranchTip(argument)),
            _ => false,
        };
    }

    /// <summary>Stops the feed consumer. Safe to call more than once.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _cancellation?.Cancel();
        _cancellation?.Dispose();
    }

    private static string ReadRepository(JsonElement model)
    {
        if (model.ValueKind != JsonValueKind.Object)
        {
            throw new PaneModelException("the model must be an object");
        }

        var repository = PaneValues.RequiredString(model, "repository");
        if (string.IsNullOrWhiteSpace(repository))
        {
            throw new PaneModelException("model field 'repository' must not be empty");
        }

        return repository;
    }

    private async Task ConsumeAsync(IGitSolarSource source, string repository, CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        try
        {
            await foreach (var update in source.UpdatesAsync(repository, token).ConfigureAwait(false))
            {
                ApplyUpdate(update);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return;
        }
        catch (Exception)
        {
            // A failed feed reads as a dropped connection: the last state stays, marked stale.
        }

        // The feed ended without being cancelled, so the connection is gone.
        if (!token.IsCancellationRequested)
        {
            ApplyUpdate(new GitSolarUpdate.Disconnected());
        }
    }

    private void ApplyUpdate(GitSolarUpdate update)
    {
        var applied = false;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            applied = _model.Apply(update);
            if (applied && update is GitSolarUpdate.Snapshot snapshot)
            {
                BuildRows(snapshot.Value);
                _list = BuildList(_model.SelectedSha);
            }
        }

        if (applied)
        {
            RaiseAll();
        }
    }

    /// <summary>Builds the rows in time order. Each commit's kind is its rank, so the list's kind-then-label sort keeps this order.</summary>
    private void BuildRows(GitSolarSnapshot snapshot)
    {
        var comets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var edge in snapshot.Edges)
        {
            if (edge.IsComet)
            {
                comets.Add(edge.FromSha);
                comets.Add(edge.ToSha);
            }
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<GitSolarListItem>(snapshot.Commits.Count);
        var inputs = new List<AccessibleNodeInput>(snapshot.Commits.Count);
        foreach (var index in GitSolarTimeline.NewestFirst(snapshot))
        {
            var commit = snapshot.Commits[index];
            if (!seen.Add(commit.Sha))
            {
                continue;
            }

            var row = new GitSolarListItem(
                commit.Sha,
                commit.Sha.Length > 7 ? commit.Sha[..7] : commit.Sha,
                commit.Subject,
                commit.Branch,
                comets.Contains(commit.Sha));
            rows.Add(row);
            inputs.Add(new AccessibleNodeInput(
                commit.Sha,
                row.Text,
                (rows.Count - 1).ToString("D8", CultureInfo.InvariantCulture)));
        }

        _items = rows;
        _inputs = inputs;
        _hasSnapshot = true;
    }

    private AccessibleNodeList BuildList(string? selectedSha) => AccessibleNodeList.Create(_inputs, selectedSha);

    /// <summary>Computes the target row under the lock, then selects it. An out-of-range target is ignored.</summary>
    private void MoveTo(Func<int> pick)
    {
        string? sha;
        lock (_gate)
        {
            var index = pick();
            sha = index >= 0 && index < _items.Count ? _items[index].Sha : null;
        }

        if (sha is not null)
        {
            Select(model => model.SelectBySha(sha));
        }
    }

    /// <summary>Applies a selection change to the model and rebuilds the list's focus. Raises events only when the selection moved.</summary>
    private bool Select(Func<IGitSolarModel, bool> select)
    {
        bool accepted;
        var changed = false;
        lock (_gate)
        {
            var before = _model.SelectedSha;
            accepted = select(_model);
            if (_model.SelectedSha != before)
            {
                _list = BuildList(_model.SelectedSha);
                changed = true;
            }
        }

        if (changed)
        {
            Raise(nameof(SelectedIndex));
            Raise(nameof(SelectedSha));
        }

        return accepted;
    }

    private void RaiseAll()
    {
        Raise(nameof(Items));
        Raise(nameof(SelectedIndex));
        Raise(nameof(SelectedSha));
        Raise(nameof(IsStale));
        Raise(nameof(StatusText));
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
