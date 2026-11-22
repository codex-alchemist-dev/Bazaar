namespace Bazaar.Contracts.Community;

/// <summary>HTTP routes of the community module, shared by the server and the clients so the two cannot drift.</summary>
public static class CommunityRoutes
{
    /// <summary>Route template of the owner's signed reply to a draft-review card: <c>POST /community/drafts/{id}/decision</c>.</summary>
    public const string DraftDecisionTemplate = "community/drafts/{id}/decision";

    /// <summary>Route template of one draft's review card: <c>GET /community/drafts/{id}</c> [PROPOSED].</summary>
    public const string DraftTemplate = "community/drafts/{id}";

    /// <summary>Route of the open-draft list: <c>GET /community/drafts?status=pending</c> [PROPOSED].</summary>
    public const string DraftList = "community/drafts";

    /// <summary>Route of the owner-only community log page: <c>GET /community/log?agentId=&amp;platform=&amp;limit=&amp;cursor=</c> (doc 36 WP9).</summary>
    public const string Log = "community/log";

    /// <summary>The relative route for one draft. The body is a <see cref="DraftReviewDecisionDto"/>.</summary>
    public static string DraftDecision(Guid draftId) => $"community/drafts/{draftId:D}/decision";

    /// <summary>The relative route to read one draft's review card (a <see cref="DraftReviewCardDto"/>).</summary>
    public static string Draft(Guid draftId) => $"community/drafts/{draftId:D}";
}
