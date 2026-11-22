using System.Text.Json;

namespace Bazaar.Contracts.Panes;

/// <summary>
/// A declarative pane the server sends to clients (doc 20). The client maps <see cref="Kind"/> to a
/// view model; an unknown kind falls back to the generic tree view. The model is data, never code.
/// </summary>
/// <param name="Kind">Namespaced pane kind, for example <see cref="PaneKinds.Kanban"/> or <c>godot.scene-tree</c>.</param>
/// <param name="Title">Heading shown on the pane.</param>
/// <param name="Capability">Capability that backs the pane, or an empty string for first-party panes.</param>
/// <param name="Model">Kind-specific payload.</param>
public sealed record PaneDescriptor(string Kind, string Title, string Capability, JsonElement Model);

/// <summary>Kinds of the first-party panes every client ships with.</summary>
public static class PaneKinds
{
    /// <summary>Kanban dependency board (doc 12).</summary>
    public const string Kanban = "bazaar.kanban";

    /// <summary>Calcinato terminal pane: a shell or server terminal in the modal emulator (doc 55, section 5.4).</summary>
    public const string Terminal = "bazaar.terminal";

    /// <summary>Start-screen dashboard of what changed while away (CLAUDE.md, client start screen).</summary>
    public const string Dashboard = "bazaar.dashboard";

    /// <summary>Constellation 2D view of the Vault's knowledge graph (doc 19).</summary>
    public const string Constellation = "bazaar.constellation";

    /// <summary>Vault search pane: search the Vault over GET /vault/search and open a result (doc 18).</summary>
    public const string VaultSearch = "bazaar.vault-search";

    /// <summary>RyuseiFuda micro schedule: the per-agent triggers that wake agents (doc 38, layer 2).</summary>
    public const string RyuseiSchedule = "bazaar.ryusei-schedule";

    /// <summary>RyuseiFuda objective roadmap: phases, objectives and task progress (doc 38, layer 3).</summary>
    public const string Roadmap = "bazaar.roadmap";

    /// <summary>RyuseiFuda Workday: the macro board's team schedule, grouped by owner (doc 38, layer 1).</summary>
    public const string Workday = "bazaar.ryusei-workday";

    /// <summary>Health: per-project health block from the metric rollups (doc 25, Health and Analytics).</summary>
    public const string Health = "bazaar.health";

    /// <summary>Analytics: rollup trends, project morale signals and the optional per-agent view (doc 25).</summary>
    public const string Analytics = "bazaar.analytics";

    /// <summary>Digital Twin project card: facts and prose pages (doc 25).</summary>
    public const string Twin = "bazaar.twin";

    /// <summary>Bazaar Archive: read-only book with chapter navigation (doc 25).</summary>
    public const string Archive = "bazaar.archive";

    /// <summary>Company Memory decision chain (doc 25).</summary>
    public const string CompanyMemory = "bazaar.company-memory";

    /// <summary>Hive Mind owner grant matrix and labelled copies (doc 25).</summary>
    public const string Hive = "bazaar.hive";
    /// <summary>Community draft review: outbound posts waiting for the owner's approval (doc 36, work package 8).</summary>
    public const string CommunityDrafts = "bazaar.community-drafts";

    /// <summary>Community log: what agents did on outside platforms, never what they said (doc 36, work package 9).</summary>
    public const string CommunityLog = "bazaar.community-log";

    /// <summary>Org chart: reports-to and loan edges, by company or project lens (doc 24 section 5).</summary>
    public const string Org = "bazaar.org";

    /// <summary>Company calendar: a month grid of plans, releases and vacations (doc 24 section 5).</summary>
    public const string Calendar = "bazaar.calendar";

    /// <summary>Interrupt stacks: agents with a pushed frame, depth 0 to 2 (doc 24 section 5).</summary>
    public const string InterruptStack = "bazaar.interrupt-stack";

    /// <summary>Approvals on the phone: pending approval cards with approve, reject and detail (doc 29 Approvals).</summary>
    public const string Approvals = "bazaar.approvals";

    /// <summary>Git Solar System: the solar-system view of one repository's history (doc 19, Git Solar System client module).</summary>
    public const string GitSolarSystem = "bazaar.git-solar-system";

    /// <summary>Agent analytics: per-agent throughput, review pass rate and revert rate, framed as what would help the company, never a ranking (doc 25).</summary>
    public const string AgentAnalytics = "bazaar.agent-analytics";
}
