using Bazaar.Server.Security.Policy;

namespace Bazaar.Server.Community.Policy;

/// <summary>
/// Builds the tool registrations for community actions (doc 36, "Approval tiers"). One tool per (platform, action),
/// registered in the same <see cref="CapabilityRegistry"/> as every other tool, so the policy engine decides them.
/// </summary>
public static class CommunityCapabilities
{
    /// <summary>
    /// Builds one <see cref="ToolCapability"/> per platform and action.
    /// </summary>
    /// <param name="platforms">Platform keys, such as <c>github</c>. Each is validated by <see cref="CommunityTierMap.ToolName"/>.</param>
    /// <param name="rateFor">Optional quota for an action, passed through to the capability. Null means no quota.</param>
    /// <returns>Every capability in platform order, then action order. A repeated platform key (for example <c>github</c> twice,
    /// or <c>github</c> and <c>GitHub</c>) repeats names; <see cref="CapabilityRegistry.Create"/> rejects those.</returns>
    /// <exception cref="ArgumentException">A platform key is not valid.</exception>
    public static IReadOnlyList<ToolCapability> Create(IEnumerable<string> platforms, Func<CommunityAction, RateRule?>? rateFor = null)
    {
        ArgumentNullException.ThrowIfNull(platforms);

        var tools = new List<ToolCapability>();
        foreach (var platform in platforms)
        {
            foreach (var action in Enum.GetValues<CommunityAction>())
            {
                tools.Add(new ToolCapability(
                    CommunityTierMap.ToolName(action, platform),
                    CommunityTierMap.TierFor(action),
                    ClassFor(action),
                    RolesFor(action),
                    rateFor?.Invoke(action)));
            }
        }

        return tools;
    }

    /// <summary>
    /// The side effect of an action. Doc 36 has no external-write class, so writes use <see cref="CapabilityClass.Post"/>
    /// (publishing where others see it), <see cref="CapabilityClass.Send"/> (outreach email) or
    /// <see cref="CapabilityClass.CreateAccount"/>.
    /// </summary>
    private static CapabilityClass ClassFor(CommunityAction action) => action switch
    {
        CommunityAction.Read => CapabilityClass.Read,
        CommunityAction.ReplyApprovedThread => CapabilityClass.Post,
        CommunityAction.NewPost => CapabilityClass.Post,
        CommunityAction.NewIssue => CapabilityClass.Post,
        CommunityAction.NewPullRequest => CapabilityClass.Post,
        CommunityAction.EditOwn => CapabilityClass.Post,
        CommunityAction.WithdrawOwn => CapabilityClass.Post,
        CommunityAction.CreateAccount => CapabilityClass.CreateAccount,
        CommunityAction.OutreachEmail => CapabilityClass.Send,
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown community action."),
    };

    /// <summary>
    /// Reads may run in a reader session (untrusted input). Every write runs only in an actor session, because a reader
    /// holds no outward tools (doc 15, "Reader and actor sessions").
    /// </summary>
    private static SessionRoles RolesFor(CommunityAction action) =>
        action == CommunityAction.Read
            ? SessionRoles.Reader | SessionRoles.Actor
            : SessionRoles.Actor;
}
