using System.Globalization;
using System.Text.Json;
using Bazaar.Server.Council;
using Bazaar.Server.Council.Service;

namespace Bazaar.Server.Council.Tests.Service;

/// <summary>
/// Pins the exact GC event payload bytes. Council events are read by other modules and by the Vault, so the JSON
/// shape (snake_case names, property order, null handling, nested objects) must not change when the serializer changes.
/// Random values (vote ids, the harness GC id, reveal seeds) are replaced with fixed tokens before comparing.
/// </summary>
public class CouncilEventPayloadTests
{
    /// <summary>Replaces the random vote id, the harness GC id and the reveal seed (when present) with fixed tokens.</summary>
    private static string Normalise(string payload, Guid voteId, Guid gcId)
    {
        var text = payload
            .Replace(voteId.ToString("D"), "VOTE_ID", StringComparison.Ordinal)
            .Replace(gcId.ToString("D"), "GC_ID", StringComparison.Ordinal);
        using var document = JsonDocument.Parse(payload);
        if (document.RootElement.TryGetProperty("reveal_seed", out var seed))
        {
            text = text.Replace(
                seed.GetInt64().ToString(CultureInfo.InvariantCulture),
                "SEED",
                StringComparison.Ordinal);
        }

        return text;
    }

    [Fact]
    public async Task Open_payload_is_byte_for_byte()
    {
        var h = new CouncilHarness();
        h.AddAgents("ada", "cal");
        h.AddOwner();
        var id = await h.CreateService().OpenAsync("ada", h.Motion(), CancellationToken.None);

        var payload = h.Events.OfKind("council.open").Single().PayloadJson;

        Assert.Equal(
            "{\"vote_id\":\"VOTE_ID\",\"gc_id\":\"GC_ID\",\"subject_ref\":\"task/42\",\"kind\":\"approve_reject\",\"motion_class\":\"ordinary\",\"high_impact\":false,\"options\":[],\"proposer_id\":\"ada\",\"subject_author_id\":null,\"eligible_voter_ids\":[\"cal\"],\"deadline_at\":\"2026-11-02T09:00:00+00:00\",\"reveal_seed\":SEED}",
            Normalise(payload, id, h.GcId));
    }

    [Fact]
    public async Task Withdraw_writes_the_transition_payload_byte_for_byte()
    {
        var h = new CouncilHarness();
        h.AddThreeVoters();
        var id = await h.CreateService().OpenAsync("ada", h.Motion(), CancellationToken.None);
        await h.CreateService().WithdrawAsync(id, "ada", CancellationToken.None);

        var payload = h.Events.OfKind("council.transition").Single().PayloadJson;

        Assert.Equal(
            "{\"vote_id\":\"VOTE_ID\",\"from\":\"collecting\",\"to\":\"withdrawn\",\"reason\":\"withdrawn by ada\",\"version\":2,\"deadline_at\":\"2026-11-02T09:00:00+00:00\",\"extensions\":0}",
            Normalise(payload, id, h.GcId));
    }

    [Fact]
    public async Task Reveal_and_decision_payloads_are_byte_for_byte()
    {
        // A fixed reason order keeps the reveal independent of the random seed.
        var h = new CouncilHarness { Options = new CouncilOptions { ReasonOrder = ReasonOrder.Fixed } };
        var id = await OpenThreeVoterAsync(h);
        var service = h.CreateService();
        await service.SubmitBallotAsync(id, "cal", Ballots.Independent("cal", "yes"), CancellationToken.None);
        await service.SubmitBallotAsync(id, "dot", Ballots.Independent("dot", "yes"), CancellationToken.None);
        await service.SubmitBallotAsync(id, "eve", Ballots.Independent("eve", "no"), CancellationToken.None);

        var reveal = h.Events.OfKind("council.reveal").Single().PayloadJson;
        var decision = h.Events.OfKind("council.decision").Single().PayloadJson;

        Assert.Equal(
            "{\"vote_id\":\"VOTE_ID\",\"gc_id\":\"GC_ID\",\"reveal_seed\":SEED,\"reason_order\":[\"cal\",\"dot\",\"eve\"],\"tally\":{\"outcome\":\"decided\",\"choice\":\"yes\",\"escalation_reason\":null,\"counts\":{\"yes\":2,\"no\":1,\"abstain\":0}},\"reasons\":[{\"voter_id\":\"cal\",\"choice\":\"yes\",\"reasoning\":\"The evidence points this way. The cost is acceptable.\"},{\"voter_id\":\"dot\",\"choice\":\"yes\",\"reasoning\":\"The evidence points this way. The cost is acceptable.\"},{\"voter_id\":\"eve\",\"choice\":\"no\",\"reasoning\":\"The evidence points this way. The cost is acceptable.\"}]}",
            Normalise(reveal, id, h.GcId));
        Assert.Equal(
            "{\"vote_id\":\"VOTE_ID\",\"gc_id\":\"GC_ID\",\"decided_by\":\"tally\",\"choice\":\"yes\",\"owner_reasoning\":null,\"vault_decision_no\":1,\"final_counts\":{\"yes\":2,\"no\":1},\"voters\":[{\"voter_id\":\"cal\",\"independent_choice\":\"yes\",\"followup_choice\":null,\"final_choice\":\"yes\",\"reasoning\":\"The evidence points this way. The cost is acceptable.\"},{\"voter_id\":\"dot\",\"independent_choice\":\"yes\",\"followup_choice\":null,\"final_choice\":\"yes\",\"reasoning\":\"The evidence points this way. The cost is acceptable.\"},{\"voter_id\":\"eve\",\"independent_choice\":\"no\",\"followup_choice\":null,\"final_choice\":\"no\",\"reasoning\":\"The evidence points this way. The cost is acceptable.\"}]}",
            Normalise(decision, id, h.GcId));
    }

    private static async Task<Guid> OpenThreeVoterAsync(CouncilHarness h)
    {
        h.AddThreeVoters();
        return await h.CreateService().OpenAsync("ada", h.Motion(), CancellationToken.None);
    }
}
