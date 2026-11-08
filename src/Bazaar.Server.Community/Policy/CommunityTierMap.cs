using Bazaar.Server.Security.Policy;

namespace Bazaar.Server.Community.Policy;

/// <summary>
/// Shipped default tier for each community action (doc 36, "Approval tiers for community actions"). Operators may
/// change a row per platform; this map is the default only. Every write is at least Tier 2 until the hand-over exists.
/// </summary>
public static class CommunityTierMap
{
    /// <summary>The tier an action runs at before session taint is considered.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The action is not in the map.</exception>
    public static ToolTier TierFor(CommunityAction action) => action switch
    {
        // Tier 0: search and read public content.
        CommunityAction.Read => ToolTier.Autonomous,

        // Tier 1 in doc 36, but the Q1 default keeps it at Tier 2 (owner approves each reply).
        CommunityAction.ReplyApprovedThread => ToolTier.OwnerApproves,

        CommunityAction.NewPost => ToolTier.OwnerApproves,
        CommunityAction.NewIssue => ToolTier.OwnerApproves,
        CommunityAction.NewPullRequest => ToolTier.OwnerApproves,
        CommunityAction.EditOwn => ToolTier.OwnerApproves,
        CommunityAction.WithdrawOwn => ToolTier.OwnerApproves,
        CommunityAction.CreateAccount => ToolTier.OwnerApproves,
        CommunityAction.OutreachEmail => ToolTier.OwnerApproves,

        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown community action."),
    };

    /// <summary>
    /// Tool name the model calls for one action: <c>community.&lt;platform&gt;.&lt;action in snake_case&gt;</c>.
    /// The platform is lower-cased and must be letters, digits or dashes.
    /// </summary>
    /// <exception cref="ArgumentException">The platform is empty or has characters outside letters, digits and dashes.</exception>
    public static string ToolName(CommunityAction action, string platform)
    {
        ArgumentNullException.ThrowIfNull(platform);

        var normalised = platform.ToLowerInvariant();
        if (normalised.Length == 0 || !normalised.All(IsPlatformCharacter))
        {
            throw new ArgumentException(
                $"Platform '{platform}' must be non-empty and contain only letters, digits and dashes.",
                nameof(platform));
        }

        return $"community.{normalised}.{ToSnakeCase(action)}";
    }

    private static bool IsPlatformCharacter(char c) =>
        c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-';

    private static string ToSnakeCase(CommunityAction action)
    {
        var name = action.ToString();
        var builder = new System.Text.StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0)
                {
                    builder.Append('_');
                }

                builder.Append(char.ToLowerInvariant(c));
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }
}
