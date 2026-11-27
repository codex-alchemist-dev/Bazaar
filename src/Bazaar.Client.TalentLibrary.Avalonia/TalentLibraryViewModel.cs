using System.ComponentModel;
using System.Windows.Input;

namespace Bazaar.Client.TalentLibrary.Avalonia;

/// <summary>One prompt line as the view draws it: a gutter mark, the text and a colour.</summary>
public sealed record DiffLineRow(string Mark, string Text, string Color);

/// <summary>A version button in the version list.</summary>
public sealed record VersionRow(LibraryVersion Version, string Label);

/// <summary>
/// The Talent library view (docs/design/25, Talent Pool: library with version diffs). Read only: it lists templates, lists the
/// versions of one, and shows what changed between two. It approves and hires nothing; each version still needs its own Council
/// vote and a tool grant needs the owner's approval card.
/// </summary>
public sealed class TalentLibraryViewModel : INotifyPropertyChanged
{
    private readonly ITalentLibrarySource _source;
    private IReadOnlyList<LibraryEntry> _entries = [];
    private IReadOnlyList<VersionRow> _versions = [];
    private IReadOnlyList<DiffLineRow> _lines = [];
    private IReadOnlyList<string> _summary = [];
    private LibraryEntry? _selected;
    private LibraryVersion? _from;
    private string _notice = "";
    private string _diffTitle = "";

    public TalentLibraryViewModel(ITalentLibrarySource source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        RefreshCommand = new SimpleCommand(_ => _ = RefreshAsync());
        SelectCommand = new SimpleCommand(p => { if (p is LibraryEntry e) { _ = SelectAsync(e); } });
        VersionCommand = new SimpleCommand(p => { if (p is VersionRow v) { _ = PickVersionAsync(v.Version); } });
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ICommand RefreshCommand { get; }

    public ICommand SelectCommand { get; }

    public ICommand VersionCommand { get; }

    public IReadOnlyList<LibraryEntry> Entries => _entries;

    public IReadOnlyList<VersionRow> Versions => _versions;

    public IReadOnlyList<DiffLineRow> Lines => _lines;

    /// <summary>One sentence per non-prompt change: tools, models, files, name.</summary>
    public IReadOnlyList<string> Summary => _summary;

    public LibraryEntry? Selected => _selected;

    public string DiffTitle => _diffTitle;

    public string Notice => _notice;

    public bool HasNotice => _notice.Length > 0;

    public bool HasDiff => _diffTitle.Length > 0;

    /// <summary>Loads the library and clears any open template.</summary>
    public async Task RefreshAsync()
    {
        try
        {
            _entries = await _source.ListAsync(CancellationToken.None);
            _notice = _entries.Count == 0 ? "No templates yet." : "";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _notice = "Could not reach the server.";
        }

        _selected = null;
        _from = null;
        _versions = [];
        ClearDiff();
        Raise();
    }

    /// <summary>Opens one template: lists its versions and, when it has two or more, diffs the latest against the one before.</summary>
    public async Task SelectAsync(LibraryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _selected = entry;
        _from = null;
        ClearDiff();
        try
        {
            var versions = await _source.VersionsAsync(entry.TemplateId, CancellationToken.None);
            _versions = [.. versions.Select(v => new VersionRow(v, $"Version {v.Version}: {Describe(v.Standing)}"))];
            _notice = versions.Count == 0 ? "This template is no longer in the library." : "";
            if (versions.Count >= 2)
            {
                await ShowDiffAsync(versions[^2].Version, versions[^1].Version);
                return;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _versions = [];
            _notice = "Could not reach the server.";
        }

        Raise();
    }

    /// <summary>
    /// Version buttons work in pairs: the first tap marks the older version, the second shows the diff against it. Tapping an
    /// older version after a newer one still compares the two in order.
    /// </summary>
    public async Task PickVersionAsync(LibraryVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        if (_selected is null)
        {
            return;
        }

        if (_from is null || _from.Version == version.Version)
        {
            _from = version;
            _notice = $"Version {version.Version} marked. Tap another version to compare.";
            Raise();
            return;
        }

        var (older, newer) = _from.Version < version.Version ? (_from.Version, version.Version) : (version.Version, _from.Version);
        _from = null;
        await ShowDiffAsync(older, newer);
    }

    private async Task ShowDiffAsync(int from, int to)
    {
        var id = _selected!.TemplateId;
        try
        {
            var diff = await _source.DiffAsync(id, from, to, CancellationToken.None);
            if (diff is null)
            {
                ClearDiff();
                _notice = "Those versions could not be compared.";
            }
            else
            {
                _notice = "";
                _diffTitle = $"Version {diff.FromVersion} to version {diff.ToVersion}";
                _lines = [.. diff.PromptLines.Select(ToRow)];
                _summary = Summarize(diff);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            ClearDiff();
            _notice = "Could not reach the server.";
        }

        Raise();
    }

    private static DiffLineRow ToRow(LibraryLine l) => l.Kind switch
    {
        "added" => new DiffLineRow("+ ", l.Text, "ForestGreen"),
        "removed" => new DiffLineRow("- ", l.Text, "IndianRed"),
        _ => new DiffLineRow("  ", l.Text, "Gray"),
    };

    private static List<string> Summarize(LibraryDiff d)
    {
        var s = new List<string>();
        if (d.OldName is not null || d.NewName is not null)
        {
            s.Add($"Renamed from {d.OldName} to {d.NewName}.");
        }

        Add(s, "Tools added", d.ToolsAdded);
        Add(s, "Tools removed", d.ToolsRemoved);
        Add(s, "Models added", d.ModelsAdded);
        Add(s, "Models removed", d.ModelsRemoved);
        Add(s, "Files added", d.FilesAdded);
        Add(s, "Files removed", d.FilesRemoved);
        if (d.NeedsNewToolGrant)
        {
            s.Add("The newer version asks for new tools, so it needs its own vote and a fresh tool approval from you.");
        }

        if (s.Count == 0 && d.PromptLines.All(l => l.Kind == "same"))
        {
            s.Add("These versions are the same.");
        }

        return s;
    }

    private static void Add(List<string> into, string label, string[] items)
    {
        if (items.Length > 0)
        {
            into.Add($"{label}: {string.Join(", ", items)}.");
        }
    }

    private static string Describe(string standing) => standing switch
    {
        "released" => "released",
        "vote_pending" => "vote open",
        "blocked" => "vote rejected",
        _ => "quarantined",
    };

    private void ClearDiff()
    {
        _diffTitle = "";
        _lines = [];
        _summary = [];
    }

    private void Raise() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    private sealed class SimpleCommand(Action<object?> run) : ICommand
    {
#pragma warning disable CS0067 // Always executable; the event is part of ICommand.
        public event EventHandler? CanExecuteChanged;
#pragma warning restore CS0067

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => run(parameter);
    }
}
