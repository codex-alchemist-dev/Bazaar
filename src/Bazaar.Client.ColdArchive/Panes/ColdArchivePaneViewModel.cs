using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.ColdArchive.Panes;

/// <summary>
/// The owner-only Archive pane (pane kind <see cref="PaneKinds.ColdArchive"/>, doc 25 "Archive cold storage", PROPOSED): archived
/// units, archive, verify and restore-report actions on the selected unit, and search over archived text. Every action reports in
/// <see cref="Status"/>; a refusal shows its code and never throws out of the pane. Without a client the pane says so.
/// </summary>
public sealed class ColdArchivePaneViewModel : IPaneViewModel, INotifyPropertyChanged
{
    private readonly IColdArchiveClient? _client;
    private string _status;
    private string _kind = "gc";
    private string _unitId = string.Empty;
    private string _query = string.Empty;
    private bool _busy;

    public ColdArchivePaneViewModel(PaneDescriptor descriptor, IColdArchiveClient? client)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        Kind = descriptor.Kind;
        Title = "Archive · Cold storage";
        _client = client;
        _status = client is null ? "No archive connection." : "Ready.";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Kind { get; }

    public string Title { get; }

    public ObservableCollection<ArchiveUnitInfo> Units { get; } = [];

    public ObservableCollection<ArchiveHit> Hits { get; } = [];

    /// <summary>The line the view shows under the actions.</summary>
    public string Status { get => _status; private set => Set(ref _status, value); }

    /// <summary>The unit kind the next action applies to: <c>gc</c> or <c>project</c>.</summary>
    public string UnitKind { get => _kind; set => Set(ref _kind, value); }

    public string UnitId { get => _unitId; set => Set(ref _unitId, value); }

    public string Query { get => _query; set => Set(ref _query, value); }

    public bool Busy { get => _busy; private set => Set(ref _busy, value); }

    /// <summary>Selects an archived unit as the target of the actions.</summary>
    public void Select(ArchiveUnitInfo unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        UnitKind = unit.Kind;
        UnitId = unit.Id;
    }

    public Task RefreshAsync(CancellationToken ct = default) => RunAsync(async client =>
    {
        var units = await client.ListAsync(ct).ConfigureAwait(false);
        Units.Clear();
        foreach (var u in units)
        {
            Units.Add(u);
        }

        return units.Count == 0 ? "Nothing is archived yet." : string.Create(CultureInfo.InvariantCulture, $"{units.Count} archived.");
    });

    public Task ArchiveAsync(CancellationToken ct = default) => RunUnitAsync(async (client, k, id) =>
    {
        var r = await client.ArchiveAsync(k, id, ct).ConfigureAwait(false);
        Units.Add(new ArchiveUnitInfo(r.Kind, r.Id));
        return string.Create(CultureInfo.InvariantCulture, $"Archived {r.Records} records in {r.Segments} segments. Root {Short(r.RootHash)}.");
    });

    public Task VerifyAsync(CancellationToken ct = default) => RunUnitAsync(async (client, k, id) =>
    {
        var r = await client.VerifyAsync(k, id, ct).ConfigureAwait(false);
        return r.Ok ? $"Verified. Root {Short(r.RootHash ?? string.Empty)}." : "Verify failed: " + string.Join("; ", r.Problems);
    });

    public Task RestoreAsync(CancellationToken ct = default) => RunUnitAsync(async (client, k, id) =>
    {
        var r = await client.RestoreAsync(k, id, ct).ConfigureAwait(false);
        return string.Create(CultureInfo.InvariantCulture, $"Restore report: {r.Restored} restored, {r.AlreadyPresent} already live, {r.Conflicts.Count} conflicts.")
            + (r.Conflicts.Count > 0 ? " " + string.Join(", ", r.Conflicts) : string.Empty);
    });

    public Task SearchAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(Query))
        {
            Status = "Type something to search.";
            return Task.CompletedTask;
        }

        return RunAsync(async client =>
        {
            var hits = await client.SearchAsync(Query.Trim(), ct).ConfigureAwait(false);
            Hits.Clear();
            foreach (var h in hits)
            {
                Hits.Add(h);
            }

            return hits.Count == 0 ? "No archived text matches." : string.Create(CultureInfo.InvariantCulture, $"{hits.Count} matches.");
        });
    }

    private Task RunUnitAsync(Func<IColdArchiveClient, string, string, Task<string>> action)
    {
        if (string.IsNullOrWhiteSpace(UnitId))
        {
            Status = "Pick or type a unit id first.";
            return Task.CompletedTask;
        }

        var kind = UnitKind;
        var id = UnitId.Trim();
        return RunAsync(client => action(client, kind, id));
    }

    private async Task RunAsync(Func<IColdArchiveClient, Task<string>> action)
    {
        if (_client is null)
        {
            Status = "No archive connection.";
            return;
        }

        Busy = true;
        try
        {
            Status = await action(_client).ConfigureAwait(true);
        }
        catch (ArchiveRequestException ex)
        {
            Status = ex.Code switch
            {
                "forbidden" => "Only the owner can use the archive.",
                "already_archived" => "That unit is already archived.",
                "cannot_reinsert" => "A record is missing from the live log and cannot be put back.",
                "not_configured" => "The server has no archive store configured.",
                _ => "Refused: " + ex.Code + ".",
            };
        }
        catch (HttpRequestException)
        {
            Status = "Could not reach the server.";
        }
        finally
        {
            Busy = false;
        }
    }

    private static string Short(string hash) => hash.Length <= 12 ? hash : hash[..12];

    private void Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
