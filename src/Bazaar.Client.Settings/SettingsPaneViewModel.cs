using Bazaar.Client.Abstractions.Panes;
using Bazaar.Contracts.Panes;
using Bazaar.Server.Settings;

namespace Bazaar.Client.Settings;

/// <summary>The settings of one area (the first dotted segment of the key), rows in name order.</summary>
public sealed record SettingGroup(string Area, IReadOnlyList<SettingRowViewModel> Rows);

/// <summary>
/// The settings pane (doc 03 settings registry; doc 28 start of the app). It lists what the server's registry holds, grouped by
/// area, and writes through <see cref="ISettingsClient"/>. The pane model carries no data: the registry is the source.
/// </summary>
public sealed class SettingsPaneViewModel : IPaneViewModel
{
    /// <summary>The line shown when no server is serving settings.</summary>
    public const string NotConnectedText = "Not connected. Settings load from the server, and none is connected.";

    private readonly ISettingsClient _client;
    private List<SettingRowViewModel> _rows = [];
    private Task? _loading;
    private string _search = string.Empty;

    public SettingsPaneViewModel(PaneDescriptor descriptor, ISettingsClient client)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(client);

        Kind = descriptor.Kind;
        Title = string.IsNullOrWhiteSpace(descriptor.Title) || descriptor.Title == descriptor.Kind ? "Settings" : descriptor.Title;
        _client = client;
        StatusText = "Loading settings.";
    }

    /// <summary>Raised when the list, filter or status changed; a view re-renders the list. Row edits raise the row's own event instead.</summary>
    public event Action? Changed;

    public string Kind { get; }

    public string Title { get; }

    /// <summary>What the pane says about its state: loading, not connected, empty, or how many settings match.</summary>
    public string StatusText { get; private set; }

    public bool IsConnected { get; private set; }

    /// <summary>The signed-in actor's roles, as text, or empty when not loaded.</summary>
    public string RolesText { get; private set; } = string.Empty;

    /// <summary>The search text. Every word must match the name, description or area, case-insensitively.</summary>
    public string Search
    {
        get => _search;
        set
        {
            _search = value ?? string.Empty;
            Refresh();
        }
    }

    /// <summary>The rows that match <see cref="Search"/>, grouped by area.</summary>
    public IReadOnlyList<SettingGroup> Groups { get; private set; } = [];

    /// <summary>All rows, unfiltered.</summary>
    public IReadOnlyList<SettingRowViewModel> Rows => _rows;

    /// <summary>Starts loading once and returns the same task afterwards. Never throws.</summary>
    public Task EnsureLoadedAsync() => _loading ??= LoadAsync();

    /// <summary>Reads the registry through the client. A failure becomes the status line, never an exception.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var snapshot = await _client.ListAsync(cancellationToken).ConfigureAwait(false);
            _rows = [.. snapshot.Entries.Select(e => new SettingRowViewModel(e, snapshot.Actor, _client))];
            IsConnected = true;
            RolesText = snapshot.Actor.Roles == SettingRole.None ? "no role" : snapshot.Actor.Roles.ToString();
            Refresh();
        }
        catch (SettingsNotConnectedException)
        {
            Fail(NotConnectedText);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Fail($"Settings could not be loaded: {ex.Message}");
        }
    }

    private void Fail(string text)
    {
        _rows = [];
        IsConnected = false;
        StatusText = text;
        Groups = [];
        Changed?.Invoke();
    }

    private void Refresh()
    {
        var terms = Search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var matches = _rows.Where(r => terms.All(t =>
            r.Name.Contains(t, StringComparison.OrdinalIgnoreCase)
            || r.Description.Contains(t, StringComparison.OrdinalIgnoreCase)
            || r.Area.Contains(t, StringComparison.OrdinalIgnoreCase)));
        Groups =
        [
            .. matches
                .GroupBy(r => r.Area, StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new SettingGroup(g.Key, [.. g.OrderBy(r => r.Name, StringComparer.Ordinal)])),
        ];
        var shown = Groups.Sum(g => g.Rows.Count);
        StatusText = _rows.Count == 0
            ? "The server has no settings to show."
            : shown == 0
                ? "No settings match the search."
                : $"{shown} of {_rows.Count} settings. Signed in as {RolesText}.";
        Changed?.Invoke();
    }
}
