namespace Bazaar.Server.GitHub.Events;

/// <summary>
/// Dedupe keys and the same-event test for GitHub events (doc 21, Ingress and Testing).
/// Keys: <c>github:&lt;delivery&gt;</c> for webhooks, <c>github:poll:&lt;event id&gt;</c> for polls.
/// Two events are the same logical event when all of these hold:
/// <list type="bullet">
/// <item>same repo node id, event name and action; and</item>
/// <item>one of: same source and same event id (a replay); the same non-null <see cref="GitHubEvent.ObjectNodeId"/>;
/// or, across a webhook and a poll, the same non-null PR node id and head SHA.</item>
/// </list>
/// The PR and head SHA rule needs two different sources, so two distinct webhooks for two check runs on one commit stay separate.
/// </summary>
public static class GitHubEventDedupe
{
    /// <summary>The key for one event as stored in the dedupe table.</summary>
    public static string KeyFor(GitHubEvent gitHubEvent)
    {
        ArgumentNullException.ThrowIfNull(gitHubEvent);
        ArgumentException.ThrowIfNullOrWhiteSpace(gitHubEvent.EventId);

        return gitHubEvent.Source switch
        {
            GitHubEventSource.Webhook => $"github:{gitHubEvent.EventId}",
            GitHubEventSource.Poll => $"github:poll:{gitHubEvent.EventId}",
            _ => throw new ArgumentOutOfRangeException(nameof(gitHubEvent), gitHubEvent.Source, "Unknown event source."),
        };
    }

    /// <summary>True when both events describe the same logical GitHub event (see the type summary).</summary>
    public static bool IsSameLogicalEvent(GitHubEvent a, GitHubEvent b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        if (a.RepoNodeId != b.RepoNodeId
            || a.EventName != b.EventName
            || a.Action != b.Action)
        {
            return false;
        }

        // Replay: the same delivery or poll id from the same source.
        if (a.Source == b.Source && a.EventId == b.EventId)
        {
            return true;
        }

        // The same comment, review or check run, seen through a webhook and a poll.
        if (a.ObjectNodeId is not null && a.ObjectNodeId == b.ObjectNodeId)
        {
            return true;
        }

        // A webhook and a poll for the same PR state. Only across sources, so separate deliveries stay separate.
        return a.Source != b.Source
            && a.PrNodeId is not null
            && a.PrNodeId == b.PrNodeId
            && a.HeadSha is not null
            && a.HeadSha == b.HeadSha;
    }
}
