using System.Text.Json.Nodes;
using Npgsql;

namespace Bazaar.Server.Kanban.Ryusei;

/// <summary>
/// What the RyuseiFuda tool adapters need about the agents in one call: the calling agent as an actor (null when the caller
/// is unknown) and each named target that exists (missing targets are left out, so the adapter can refuse them).
/// </summary>
public sealed record RyuseiAgentLookup(RyuseiActor? Caller, IReadOnlyList<RyuseiTargetInfo> Targets)
{
    /// <summary>
    /// The directory entry for each requested target, in request order with duplicates kept, so the trigger rules still see
    /// a duplicate target and refuse it (doc 38, DuplicateTarget). Null when any requested target is not in the directory.
    /// </summary>
    public IReadOnlyList<RyuseiTargetInfo>? TargetsFor(IReadOnlyList<Guid> requested)
    {
        ArgumentNullException.ThrowIfNull(requested);

        var infos = new List<RyuseiTargetInfo>(requested.Count);
        foreach (var id in requested)
        {
            var found = Targets.FirstOrDefault(t => t.AgentId == id);
            if (found is null)
            {
                return null;
            }

            infos.Add(found);
        }

        return infos;
    }
}

/// <summary>
/// Resolves the agents a RyuseiFuda tool call names. The tool adapters use it for the caller identity and for the target
/// project and active flag that <see cref="RyuseiTriggerService"/> checks. It is a read and changes nothing.
/// </summary>
public interface IRyuseiAgentDirectory
{
    /// <summary>Resolves the calling agent and the target agents in one lookup. Ids not found are absent from the result.</summary>
    Task<RyuseiAgentLookup> ResolveAsync(Guid callerAgentId, IReadOnlyCollection<Guid> targetAgentIds, CancellationToken ct);
}

/// <summary>
/// Reads the <c>agent</c> table (0010_agent_runtime.sql) for the tool adapters.
/// </summary>
/// <remarks>
/// <para>
/// Project link gap: no migration links an agent to a project. <c>agent</c> has no project column, and <c>org_relation</c>
/// (0018) only records loans (<c>lent_to</c>, scoped to a project) and reporting lines, not membership. So this directory
/// cannot read an agent's project from the database. Until the project link is designed (the cross-table migration 0005
/// or a membership table), the directory is constructed for one project, and every resolved agent is given that project id.
/// The tool adapters then pass the project on to the trigger service, which checks it against each target.
/// </para>
/// <para>
/// Active flag: <see cref="RyuseiTargetInfo.IsActive"/> is true only for state <c>active</c>. Created, onboarding, resting and
/// retired agents are not active, so a trigger cannot target them (doc 38, TargetNotActive). A retired caller is treated as
/// unknown and refused, because a retired agent takes no turns.
/// </para>
/// </remarks>
public sealed class SqlRyuseiAgentDirectory(NpgsqlDataSource dataSource, Guid projectId) : IRyuseiAgentDirectory
{
    private const string _sql = "SELECT agent_id, state FROM agent WHERE agent_id = ANY(@agent_ids)";

    private readonly NpgsqlDataSource _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    private readonly Guid _projectId = projectId;

    /// <inheritdoc />
    public async Task<RyuseiAgentLookup> ResolveAsync(Guid callerAgentId, IReadOnlyCollection<Guid> targetAgentIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(targetAgentIds);

        var ids = targetAgentIds.Append(callerAgentId).Distinct().ToArray();
        await using var cmd = _dataSource.CreateCommand(_sql);
        cmd.Parameters.AddWithValue("agent_ids", ids);

        var states = new Dictionary<Guid, string>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                states[reader.GetGuid(0)] = reader.GetString(1);
            }
        }

        RyuseiActor? caller = null;
        if (states.TryGetValue(callerAgentId, out var callerState) && callerState != "retired")
        {
            caller = RyuseiActor.ForAgent(callerAgentId, _projectId);
        }

        var targets = new List<RyuseiTargetInfo>();
        foreach (var id in targetAgentIds.Distinct())
        {
            if (states.TryGetValue(id, out var state))
            {
                targets.Add(new RyuseiTargetInfo(id, _projectId, state == "active"));
            }
        }

        return new RyuseiAgentLookup(caller, targets);
    }
}

/// <summary>Shared refusal shapes for the RyuseiFuda tool adapters. The text is fixed, never model text.</summary>
public static class RyuseiToolCallers
{
    /// <summary>The result for a call whose caller the directory does not know (or that is retired).</summary>
    public static JsonObject UnknownCaller() =>
        RyuseiToolContract.BuildResult(RyuseiTriggerResult.Refused(
            RyuseiRefusal.NotAuthorized, "The calling agent is not in the agent directory."));

    /// <summary>The result for a schedule call that names an agent the directory does not know.</summary>
    public static JsonObject UnknownTarget() =>
        RyuseiToolContract.BuildRefusal("Every target must be a known agent.");
}
