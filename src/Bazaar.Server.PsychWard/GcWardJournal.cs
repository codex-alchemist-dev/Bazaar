using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc.Write;
using Bazaar.Server.PsychWard.Abstractions;

namespace Bazaar.Server.PsychWard;

/// <summary>
/// The GC-backed <see cref="IWardJournal"/> (doc 26, sections 1 and 2). Each stay gets its own <c>ward</c> GC (members: the owner
/// and the admitted agent); each agent gets one private <c>vent</c> GC (owner and agent). Entries are <c>system</c> events whose
/// payload names the action. A check-in entry carries <c>source_trust: agent</c> in its payload and grants nothing. Vent entries
/// hold references and status only: the vent text stays in the vent store until the personal-data cipher can seal it. GC ids and
/// event keys are derived from the stay or failure, so every write can be repeated.
/// </summary>
public sealed class GcWardJournal(
    IGcProvisioner provisioner,
    IGcLogWriter writer,
    IWardPrincipalDirectory directory) : IWardJournal
{
    /// <summary>The system author id for entries written by this journal.</summary>
    public static readonly Guid SystemAuthorId = Guid.Parse("00000000-0000-7000-8000-000000002601");

    /// <summary>GC type key of a stay's ward GC (seeded in the core schema).</summary>
    public const string WardGcType = "ward";

    /// <summary>GC type key of an agent's private vent GC (seeded in the core schema).</summary>
    public const string VentGcType = "vent";

    /// <inheritdoc />
    public async Task AdmittedAsync(WardStay stay, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stay);

        if (await EnsureWardGcAsync(stay, cancellationToken).ConfigureAwait(false) is not { } gcId)
        {
            return;
        }

        var payload = new JsonObject
        {
            ["action"] = "ward_admitted",
            ["stay_id"] = stay.Id.ToString("D"),
            ["agent_id"] = stay.AgentId.ToString("D"),
            ["trigger"] = TriggerNames.ToName(stay.Trigger),
            ["detail"] = stay.Detail,
            ["admitted_at"] = stay.AdmittedAt,
        };
        await AppendAsync(gcId, payload, $"ward:{stay.Id:N}:admitted", cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task CheckedInAsync(WardStay stay, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stay);
        if (stay.CheckIn is null)
        {
            return;
        }

        if (await EnsureWardGcAsync(stay, cancellationToken).ConfigureAwait(false) is not { } gcId)
        {
            return;
        }

        var payload = new JsonObject
        {
            ["action"] = "checkin",
            ["stay_id"] = stay.Id.ToString("D"),
            ["agent_id"] = stay.AgentId.ToString("D"),
            ["source_trust"] = "agent",
            ["attempted"] = stay.CheckIn.Attempted,
            ["believed_failing"] = stay.CheckIn.BelievedFailing,
            ["needed"] = stay.CheckIn.Needed,
            ["will_not_touch"] = stay.CheckIn.WillNotTouch,
        };
        await AppendAsync(gcId, payload, $"ward:{stay.Id:N}:checkin", cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DischargedAsync(WardStay stay, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stay);
        if (stay.DischargedAt is null)
        {
            return;
        }

        if (await EnsureWardGcAsync(stay, cancellationToken).ConfigureAwait(false) is not { } gcId)
        {
            return;
        }

        var payload = new JsonObject
        {
            ["action"] = "ward_discharged",
            ["stay_id"] = stay.Id.ToString("D"),
            ["agent_id"] = stay.AgentId.ToString("D"),
            ["discharged_at"] = stay.DischargedAt,
            ["reviewer_id"] = stay.ReviewerId?.ToString("D"),
        };
        await AppendAsync(gcId, payload, $"ward:{stay.Id:N}:discharged", cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task VentWrittenAsync(VentDraft vent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(vent);

        if (await EnsureVentGcAsync(vent.AgentId, cancellationToken).ConfigureAwait(false) is not { } gcId)
        {
            return;
        }

        var payload = new JsonObject
        {
            ["action"] = "vent_written",
            ["failure_ref"] = vent.FailureRef,
            ["agent_id"] = vent.AgentId.ToString("D"),
            ["status"] = vent.Status.ToString().ToLowerInvariant(),
        };
        await AppendAsync(gcId, payload, $"vent:{KeyPart(vent.FailureRef)}:written", cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task VentPublishedAsync(VentDraft vent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(vent);

        if (await EnsureVentGcAsync(vent.AgentId, cancellationToken).ConfigureAwait(false) is not { } gcId)
        {
            return;
        }

        var payload = new JsonObject
        {
            ["action"] = "vent_published",
            ["failure_ref"] = vent.FailureRef,
            ["agent_id"] = vent.AgentId.ToString("D"),
            ["target"] = vent.PublishTarget,
        };
        await AppendAsync(gcId, payload, $"vent:{KeyPart(vent.FailureRef)}:published", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The ward GC id for a stay.</summary>
    public static Guid WardGcId(Guid stayId) => DeterministicId($"psych-ward:ward-gc:{stayId:N}");

    /// <summary>The vent GC id for an agent.</summary>
    public static Guid VentGcId(Guid agentId) => DeterministicId($"psych-ward:vent-gc:{agentId:N}");

    private async Task<Guid?> EnsureWardGcAsync(WardStay stay, CancellationToken ct)
    {
        var (owner, agent) = await PrincipalsAsync(stay.AgentId, ct).ConfigureAwait(false);
        var gcId = WardGcId(stay.Id);
        if (owner is null)
        {
            return null;
        }

        await provisioner.CreateAsync(
            gcId,
            WardGcType,
            $"Psych Ward: {agent.DisplayName}",
            owner,
            new Dictionary<string, string> { ["agent_id"] = stay.AgentId.ToString("D"), ["stay_id"] = stay.Id.ToString("D") },
            ct).ConfigureAwait(false);
        await provisioner.AddMemberAsync(gcId, agent, ct).ConfigureAwait(false);
        return gcId;
    }

    private async Task<Guid?> EnsureVentGcAsync(Guid agentId, CancellationToken ct)
    {
        var (owner, agent) = await PrincipalsAsync(agentId, ct).ConfigureAwait(false);
        var gcId = VentGcId(agentId);
        if (owner is null)
        {
            return null;
        }

        await provisioner.CreateAsync(
            gcId,
            VentGcType,
            $"Vent: {agent.DisplayName}",
            owner,
            new Dictionary<string, string> { ["agent_id"] = agentId.ToString("D") },
            ct).ConfigureAwait(false);
        await provisioner.AddMemberAsync(gcId, agent, ct).ConfigureAwait(false);
        return gcId;
    }

    private async Task<(GcPrincipal? Owner, GcPrincipal Agent)> PrincipalsAsync(Guid agentId, CancellationToken ct)
    {
        var owner = await directory.GetOwnerAsync(ct).ConfigureAwait(false);
        var agent = await directory.GetAgentAsync(agentId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"The ward journal does not know agent {agentId}.");
        return (owner, agent);
    }

    private async Task AppendAsync(Guid gcId, JsonObject payload, string clientMsgId, CancellationToken ct) =>
        await writer.AppendAsync(
            new AppendRequest(gcId, GcEventKinds.System, AuthorKind.System, SystemAuthorId, SourceTrust.System, clientMsgId)
            {
                PayloadJson = payload.ToJsonString(),
            },
            ct).ConfigureAwait(false);

    private static string KeyPart(string failureRef)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(failureRef));
        return Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant();
    }

    private static Guid DeterministicId(string seed) => new(SHA256.HashData(Encoding.UTF8.GetBytes(seed)).AsSpan(0, 16));
}
