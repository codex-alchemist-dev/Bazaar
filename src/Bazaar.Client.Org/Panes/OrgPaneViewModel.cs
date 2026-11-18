using System.Globalization;
using Bazaar.Client.Core.Panes;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Org.Panes;

/// <summary>
/// The read-only Org chart pane (doc 24 section 2). Model:
/// <c>{ "projects": [ { "id", "name" } ], "agents": [ { "id", "name" } ],
/// "edges": [ { "id", "kind", "fromAgentId", "toAgentId", "projectId" | null, "startsAt", "endsAt" | null } ] }</c>.
/// A lens picks the company scope (edges with no project) or one project. <c>reports_to</c> edges draw as a tree and
/// <c>lent_to</c> edges as loan rows. Cycles and edges naming unknown agents are tolerated: each agent is drawn once and
/// repeats are marked. A malformed model raises <see cref="PaneModelException"/>.
/// </summary>
public sealed class OrgPaneViewModel : IPaneViewModel
{
    /// <summary>States that Org edges carry no authority (doc 24 section 2, No authority).</summary>
    public const string NoAuthorityNotice = "Org edges grant no authority. Access comes from grants and the Identity Service.";

    private readonly TimeProvider _time;
    private readonly Dictionary<string, string> _names;

    public OrgPaneViewModel(PaneDescriptor descriptor, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        _time = time ?? TimeProvider.System;
        Kind = descriptor.Kind;
        Title = "Org";
        Snapshot = OrgSnapshotReader.Read(descriptor.Model);
        _names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var agent in Snapshot.Agents)
        {
            _names.TryAdd(agent.Id, agent.Name);
        }

        Lenses = [new OrgLens(null, "Company"), .. Snapshot.Projects.Select(p => new OrgLens(p.Id, p.Name))];
        SelectedLens = Lenses[0];
        Rebuild();
    }

    public string Kind { get; }

    public string Title { get; }

    public OrgSnapshot Snapshot { get; }

    public IReadOnlyList<OrgLens> Lenses { get; }

    public OrgLens SelectedLens { get; private set; }

    public IReadOnlyList<OrgTreeRow> Tree { get; private set; } = [];

    public IReadOnlyList<OrgLoanRow> Loans { get; private set; } = [];

    /// <summary>Switches the lens; an unknown project id keeps the current lens.</summary>
    public void SelectLens(string? projectId)
    {
        var lens = Lenses.FirstOrDefault(l => string.Equals(l.ProjectId, projectId, StringComparison.Ordinal));
        if (lens is null || lens == SelectedLens)
        {
            return;
        }

        SelectedLens = lens;
        Rebuild();
    }

    private static string Date(DateTimeOffset value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private string NameOf(string agentId) =>
        _names.TryGetValue(agentId, out var name) ? name : $"unknown agent ({agentId})";

    private void Rebuild()
    {
        var scope = Snapshot.Edges.Where(e => string.Equals(e.ProjectId, SelectedLens.ProjectId, StringComparison.Ordinal)).ToArray();
        Tree = BuildTree(scope.Where(e => e.Kind == OrgEdgeKind.ReportsTo).ToArray());
        var now = _time.GetUtcNow();
        Loans = scope.Where(e => e.Kind == OrgEdgeKind.LentTo).Select(e => ToLoan(e, now)).ToArray();
    }

    private OrgLoanRow ToLoan(OrgEdgeSummary edge, DateTimeOffset now)
    {
        var ended = edge.EndsAt is { } end && end <= now;
        var period = edge.EndsAt is { } until
            ? $"{Date(edge.StartsAt)} to {Date(until)}"
            : $"from {Date(edge.StartsAt)}, no end date";
        var line = $"{NameOf(edge.FromAgentId)} lent to {NameOf(edge.ToAgentId)} · {period}" + (ended ? " · ended" : string.Empty);
        return new OrgLoanRow(edge.Id, line, ended);
    }

    private OrgTreeRow[] BuildTree(OrgEdgeSummary[] reports)
    {
        // An agent reports to at most one agent per scope; a second edge for the same agent is ignored.
        var parentOf = new Dictionary<string, string>(StringComparer.Ordinal);
        var children = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var nodes = new List<string>();
        var known = new HashSet<string>(StringComparer.Ordinal);

        void AddNode(string id)
        {
            if (known.Add(id))
            {
                nodes.Add(id);
            }
        }

        if (SelectedLens.ProjectId is null)
        {
            foreach (var agent in Snapshot.Agents)
            {
                AddNode(agent.Id);
            }
        }

        foreach (var edge in reports)
        {
            AddNode(edge.FromAgentId);
            AddNode(edge.ToAgentId);
            if (!parentOf.TryAdd(edge.FromAgentId, edge.ToAgentId))
            {
                continue;
            }

            if (!children.TryGetValue(edge.ToAgentId, out var list))
            {
                children[edge.ToAgentId] = list = [];
            }

            list.Add(edge.FromAgentId);
        }

        var rows = new List<OrgTreeRow>();
        var drawn = new HashSet<string>(StringComparer.Ordinal);

        // Iterative depth-first walk so a deep or cyclic chain can neither overflow the stack nor loop.
        void Walk(string root)
        {
            var stack = new Stack<(string Id, int Depth)>();
            stack.Push((root, 0));
            while (stack.Count > 0)
            {
                var (id, depth) = stack.Pop();
                if (!drawn.Add(id))
                {
                    rows.Add(new OrgTreeRow(depth, id, $"{NameOf(id)} (shown above)", true));
                    continue;
                }

                rows.Add(new OrgTreeRow(depth, id, NameOf(id), false));
                if (children.TryGetValue(id, out var kids))
                {
                    for (var i = kids.Count - 1; i >= 0; i--)
                    {
                        stack.Push((kids[i], depth + 1));
                    }
                }
            }
        }

        foreach (var id in nodes.Where(n => !parentOf.ContainsKey(n)))
        {
            Walk(id);
        }

        // Whatever is left sits on a cycle with no root: start each remaining agent once.
        foreach (var id in nodes.Where(n => !drawn.Contains(n)))
        {
            Walk(id);
        }

        return [.. rows];
    }
}

/// <summary>A lens choice: the company scope (<see cref="ProjectId"/> null) or one project.</summary>
public sealed record OrgLens(string? ProjectId, string Name);

/// <summary>One tree row. <see cref="IsRepeat"/> marks an agent already drawn elsewhere (a cycle or a second path).</summary>
public sealed record OrgTreeRow(int Depth, string AgentId, string Label, bool IsRepeat);

/// <summary>One loan row. Ended loans are dimmed and read-only; every row is read-only in this pane.</summary>
public sealed record OrgLoanRow(string EdgeId, string Line, bool IsEnded);
