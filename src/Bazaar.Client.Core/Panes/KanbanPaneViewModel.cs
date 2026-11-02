using System.Text.Json;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Core.Panes;

/// <summary>One card on the board. <see cref="IsBlocked"/> is the derived Blocked marking, not a column.</summary>
public sealed record KanbanCard(string Id, string Title, bool IsBlocked);

/// <summary>One column in the fixed board order.</summary>
public sealed record KanbanColumn(string Name, IReadOnlyList<KanbanCard> Cards);

/// <summary>
/// Kanban dependency board (doc 12). Model: <c>{ "tasks": [ { "id", "title", "state", "blocked" } ] }</c>.
/// States are Backlog, Ready, In progress, Review and Done; spaces, hyphens and case are ignored.
/// </summary>
public sealed class KanbanPaneViewModel : IPaneViewModel
{
    private static readonly string[] _columnNames = ["Backlog", "Ready", "In progress", "Review", "Done"];

    public KanbanPaneViewModel(PaneDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        Kind = descriptor.Kind;
        Title = descriptor.Title;

        var tasks = PaneValues.Array(descriptor.Model, "tasks");
        var buckets = _columnNames.ToDictionary(name => name, _ => new List<KanbanCard>(), StringComparer.Ordinal);
        var skipped = 0;

        foreach (var task in tasks.EnumerateArray())
        {
            if (task.ValueKind != JsonValueKind.Object || !TryColumn(task, out var column, out var card))
            {
                skipped++;
                continue;
            }

            buckets[column].Add(card);
        }

        Columns = _columnNames.Select(name => new KanbanColumn(name, buckets[name])).ToArray();
        SkippedTaskCount = skipped;
    }

    public string Kind { get; }

    public string Title { get; }

    /// <summary>All five columns, always in board order, even when empty.</summary>
    public IReadOnlyList<KanbanColumn> Columns { get; }

    /// <summary>Tasks left out because their state was unknown or a required field was missing.</summary>
    public int SkippedTaskCount { get; }

    private static bool TryColumn(JsonElement task, out string column, out KanbanCard card)
    {
        column = string.Empty;
        card = null!;

        if (!task.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String ||
            !task.TryGetProperty("title", out var title) || title.ValueKind != JsonValueKind.String ||
            !task.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var normalized = Normalize(state.GetString()!);
        var match = _columnNames.FirstOrDefault(name => Normalize(name) == normalized);
        if (match is null)
        {
            return false;
        }

        column = match;
        card = new KanbanCard(id.GetString()!, title.GetString()!, PaneValues.Flag(task, "blocked"));
        return true;
    }

    private static string Normalize(string state) =>
        new(state.Where(c => c is not (' ' or '-' or '_')).Select(char.ToLowerInvariant).ToArray());
}
