using System.Security.Cryptography;
using System.Text;
using Bazaar.Kodo.Responder;

namespace Bazaar.Kodo.Rings;

/// <summary>What the person resuming a halted ring asks for. It carries the halt decision, so the approval is for exactly the signals that were shown.</summary>
/// <param name="Decision">The halt decision being overridden.</param>
/// <param name="Requester">Who asks, as an identifier.</param>
/// <param name="GrantedControls">The controls the requester holds (doc 48 answers; the gate only checks the name).</param>
public sealed record RingResumeRequest(RingHaltDecision Decision, string Requester, IReadOnlySet<string> GrantedControls);

/// <summary>The answer to a resume request. Identifiers only, so it can be journalled and audited with the decision.</summary>
/// <param name="Resumed">True only when the resume was explicitly approved with step-up.</param>
/// <param name="Refusal">The fixed refusal code, or null.</param>
/// <param name="ApprovalId">The approval's id, or null.</param>
/// <param name="DecisionDigest">The digest of the halt decision the answer is for.</param>
public sealed record RingResumeResult(bool Resumed, string? Refusal, string? ApprovalId, string DecisionDigest);

/// <summary>
/// The gate in front of resuming a halted ring (doc 46 section 16.6): needs the <c>kodo.runbook.approve</c> control and step-up. The
/// caller journals the answer together with the decision's signals, so a resume is audited with what was shown.
/// </summary>
public interface IRingResumeGate
{
    /// <summary>Asks for, or looks up, the approval to resume. Anything but an explicit approval is a refusal.</summary>
    ValueTask<RingResumeResult> RequestResumeAsync(RingResumeRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// A resume gate on the responder's <see cref="IApprovalGate"/> (doc 15 wiring). The approval request is bound to the halt decision's
/// digest, requires step-up and the <c>kodo.runbook.approve</c> control, and only an explicit approval with a matching digest and
/// satisfied step-up counts. A pending answer, a denial, a timeout, an error or a mismatch is a refusal.
/// </summary>
public sealed class ApprovalRingResumeGate : IRingResumeGate
{
    /// <summary>The runbook id the approval request carries, so doc 15 can recognise a ring resume.</summary>
    public const string RunbookId = "release_ring_resume";

    private readonly IApprovalGate _gate;

    /// <summary>Creates the gate.</summary>
    public ApprovalRingResumeGate(IApprovalGate gate)
    {
        ArgumentNullException.ThrowIfNull(gate);
        _gate = gate;
    }

    /// <summary>The digest an approval must echo: it binds the approval to this decision.</summary>
    public static string RequestDigest(RingHaltDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("ring_resume/" + decision.Digest)));
    }

    /// <inheritdoc />
    public async ValueTask<RingResumeResult> RequestResumeAsync(RingResumeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Decision);
        string digest = request.Decision.Digest;
        if (string.IsNullOrEmpty(request.Requester) || request.GrantedControls is null
            || !request.GrantedControls.Contains(KodoControls.RunbookApprove))
        {
            return Refuse("missing_control", digest);
        }

        string requestDigest = RequestDigest(request.Decision);
        ApprovalRequest approval = new(
            RunId: request.Decision.DecisionId,
            RunbookId: RunbookId,
            RunbookVersion: 1,
            StepId: "resume",
            Operation: "ring_resume",
            RequestDigest: requestDigest,
            Risk: RunbookRisk.High,
            RequireStepUp: true,
            Tainted: false,
            RequiredControl: KodoControls.RunbookApprove);

        ApprovalResult answer;
        try
        {
            answer = await _gate.RequestAsync(approval, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return Refuse("gate_error", digest);
        }

        if (answer is null || !string.Equals(answer.RequestDigest, requestDigest, StringComparison.Ordinal))
        {
            return Refuse("digest_mismatch", digest);
        }

        return answer.Decision switch
        {
            ApprovalDecision.Approved when !answer.StepUpSatisfied => Refuse("step_up_missing", digest),
            ApprovalDecision.Approved => new RingResumeResult(true, null, answer.ApprovalId, digest),
            ApprovalDecision.Pending => Refuse("approval_pending", digest),
            ApprovalDecision.Denied => Refuse("approval_denied", digest),
            _ => Refuse("approval_timed_out", digest),
        };
    }

    private static RingResumeResult Refuse(string code, string digest) => new(false, code, null, digest);
}
