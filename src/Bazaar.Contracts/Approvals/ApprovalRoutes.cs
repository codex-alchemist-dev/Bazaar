namespace Bazaar.Contracts.Approvals;

/// <summary>The approval routes the phone and the server share (doc 29 Approvals, PROPOSED). Paths are relative to the server base address.</summary>
public static class ApprovalRoutes
{
    /// <summary><c>GET</c>: the pending approvals, as <see cref="ApprovalCardDto"/> array.</summary>
    public const string Pending = "approvals/pending";

    /// <summary><c>POST</c> template: one signed <see cref="ApprovalDecisionDto"/> for the approval id in the path.</summary>
    public const string DecisionTemplate = "approvals/{id:guid}/decision";

    /// <summary>The decision path for one approval.</summary>
    public static string Decision(Guid approvalId) => $"approvals/{approvalId:D}/decision";
}
