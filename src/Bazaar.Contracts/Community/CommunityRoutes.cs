namespace Bazaar.Contracts.Community;

/// <summary>HTTP routes of the community module, shared by the server and the clients so the two cannot drift.</summary>
public static class CommunityRoutes
{
    /// <summary>Route template of the owner's signed reply to a draft-review card: <c>POST /community/drafts/{id}/decision</c>.</summary>
    public const string DraftDecisionTemplate = "community/drafts/{id}/decision";

    /// <summary>The relative route for one draft. The body is a <see cref="DraftReviewDecisionDto"/>.</summary>
    public static string DraftDecision(Guid draftId) => $"community/drafts/{draftId:D}/decision";
}
