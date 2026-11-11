using System.Globalization;
using System.Text.Json;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Core.Panes;

/// <summary>
/// The RyuseiFuda Workday pane (pane kind <see cref="PaneKinds.Workday"/>, doc 38 layer 1): the team schedule from the
/// macro board, grouped by owner. Model:
/// <c>{ "owners": [ { "name", "tasks": [ { "title", "state", "dueAt", "blockedBy": [ "title", ... ] } ] } ] }</c>.
/// The server orders the tasks; this view model keeps that order and only formats each line. A missing required field
/// raises <see cref="PaneModelException"/>, so the registry falls back to the generic view.
/// </summary>
public sealed class WorkdayPaneViewModel : IPaneViewModel
{
    private static readonly string[] _states = ["backlog", "ready", "in_progress", "review", "done"];

    public WorkdayPaneViewModel(PaneDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        Kind = descriptor.Kind;
        Title = "RyuseiFuda · Workday";

        var owners = new List<WorkdayOwnerRow>();
        foreach (var owner in PaneValues.Array(descriptor.Model, "owners").EnumerateArray())
        {
            if (owner.ValueKind != JsonValueKind.Object)
            {
                throw new PaneModelException("each owner must be an object");
            }

            var tasks = new List<WorkdayTaskRow>();
            foreach (var task in PaneValues.Array(owner, "tasks").EnumerateArray())
            {
                tasks.Add(ReadTask(task));
            }

            owners.Add(new WorkdayOwnerRow(PaneValues.RequiredString(owner, "name"), tasks));
        }

        Owners = owners;
    }

    public string Kind { get; }

    public string Title { get; }

    /// <summary>Owners in the order the server sent them.</summary>
    public IReadOnlyList<WorkdayOwnerRow> Owners { get; }

    private static WorkdayTaskRow ReadTask(JsonElement task)
    {
        if (task.ValueKind != JsonValueKind.Object)
        {
            throw new PaneModelException("each task must be an object");
        }

        var state = PaneValues.RequiredString(task, "state");
        if (Array.IndexOf(_states, state) < 0)
        {
            throw new PaneModelException($"unknown task state '{state}'");
        }

        string? due = null;
        if (task.TryGetProperty("dueAt", out var dueElement) && dueElement.ValueKind != JsonValueKind.Null)
        {
            if (dueElement.ValueKind != JsonValueKind.String ||
                !DateTimeOffset.TryParse(dueElement.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            {
                throw new PaneModelException("task 'dueAt' must be an ISO timestamp or null");
            }

            due = parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        var blockers = new List<string>();
        if (task.TryGetProperty("blockedBy", out _))
        {
            foreach (var blocker in PaneValues.Array(task, "blockedBy").EnumerateArray())
            {
                if (blocker.ValueKind != JsonValueKind.String)
                {
                    throw new PaneModelException("'blockedBy' must hold strings");
                }

                blockers.Add(blocker.GetString()!);
            }
        }

        return new WorkdayTaskRow(PaneValues.RequiredString(task, "title"), state, due, blockers);
    }
}

/// <summary>One owner and their tasks in the server's order.</summary>
public sealed record WorkdayOwnerRow(string Name, IReadOnlyList<WorkdayTaskRow> Tasks);

/// <summary>One task line. Blocked is shown when any blocker is listed; the column is the stored state.</summary>
public sealed record WorkdayTaskRow(string Title, string State, string? Due, IReadOnlyList<string> BlockedBy)
{
    public string Line
    {
        get
        {
            var text = $"{Title} [{State.Replace('_', ' ')}]";
            if (Due is not null)
            {
                text += $" · due {Due}";
            }

            if (BlockedBy.Count > 0)
            {
                text += " · blocked by " + string.Join(", ", BlockedBy);
            }

            return text;
        }
    }
}
