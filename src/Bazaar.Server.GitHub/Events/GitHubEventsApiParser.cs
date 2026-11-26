using System.Globalization;
using System.Text.Json;
using Bazaar.Server.GitHub.Adapter;

namespace Bazaar.Server.GitHub.Events;

/// <summary>
/// Turns an Events API page (<c>GET /repos/{owner}/{repo}/events</c>) into <see cref="GitHubEvent"/> values (doc 21, Event mapping).
/// Pure: no I/O. Event types the mapping table does not use are skipped, and one malformed event is skipped and counted
/// rather than failing the page. The Events API never carries <c>check_run</c> or <c>check_suite</c>, so those arrive only by webhook.
/// </summary>
public static class GitHubEventsApiParser
{
    /// <summary>Parse result: the supported events (newest first, as GitHub sends them) and how many entries were unusable.</summary>
    public sealed record Result(IReadOnlyList<GitHubEvent> Events, int Malformed);

    /// <param name="body">The response body: a JSON array of events.</param>
    /// <param name="repoNodeId">The repository node id. The Events API names the repo by numeric id only.</param>
    /// <param name="memberLogins">Logins treated as MEMBER because the Events API does not state the actor's association. Case-insensitive.</param>
    public static Result Parse(string body, string repoNodeId, IReadOnlySet<string> memberLogins)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoNodeId);
        ArgumentNullException.ThrowIfNull(memberLogins);

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new GitHubApiException(null, null, "GitHub events response was not JSON.", ex);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new GitHubApiException(null, null, "GitHub events response was not an array.");
            }

            var events = new List<GitHubEvent>();
            var malformed = 0;
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object || Str(element, "type") is not { } type || !IsSupported(type))
                {
                    continue;
                }

                var parsed = TryParseOne(element, type, repoNodeId, memberLogins);
                if (parsed is null)
                {
                    malformed++;
                }
                else
                {
                    events.Add(parsed);
                }
            }

            return new Result(events, malformed);
        }
    }

    private static bool IsSupported(string type) =>
        type is "PullRequestEvent" or "PullRequestReviewEvent" or "PullRequestReviewCommentEvent" or "IssueCommentEvent" or "IssuesEvent" or "PushEvent";

    private static GitHubEvent? TryParseOne(JsonElement e, string type, string repoNodeId, IReadOnlySet<string> memberLogins)
    {
        var id = Str(e, "id");
        var actor = Str(Obj(e, "actor"), "login");
        var createdAt = Time(Str(e, "created_at"));
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(actor) || createdAt is null)
        {
            return null;
        }

        var payload = Obj(e, "payload");
        var action = Str(payload, "action");
        var pull = Obj(payload, "pull_request");
        var issue = Obj(payload, "issue");
        var comment = Obj(payload, "comment");
        var review = Obj(payload, "review");

        // The object whose text or state this event is about. Its association describes that object's author, which is the
        // actor only when the actor created it. Otherwise the association is unknown and fails closed unless the login is a known member.
        JsonElement? subject = type switch
        {
            "PullRequestEvent" => pull,
            "PullRequestReviewEvent" => review,
            "PullRequestReviewCommentEvent" or "IssueCommentEvent" => comment,
            "IssuesEvent" => issue,
            _ => null,
        };

        var subjectAuthor = Str(Obj(subject, "user"), "login");
        string association;
        if (memberLogins.Contains(actor))
        {
            association = "MEMBER";
        }
        else if (string.Equals(subjectAuthor, actor, StringComparison.OrdinalIgnoreCase)
            && Str(subject, "author_association") is { Length: > 0 } declared)
        {
            association = declared;
        }
        else
        {
            association = "NONE";
        }

        // A comment on a PR arrives as IssueCommentEvent. GitHub's issue object for a PR carries the PR's node id.
        var isPrIssue = issue is { } i && i.TryGetProperty("pull_request", out _);
        var prNodeId = type switch
        {
            "PullRequestEvent" or "PullRequestReviewEvent" or "PullRequestReviewCommentEvent" => Str(pull, "node_id"),
            "IssueCommentEvent" when isPrIssue => Str(issue, "node_id"),
            _ => null,
        };
        var issueNodeId = type is "IssuesEvent" or "IssueCommentEvent" && !isPrIssue ? Str(issue, "node_id") : null;

        var (eventName, objectNodeId, body, updatedAt) = type switch
        {
            "PullRequestEvent" => ("pull_request", Str(pull, "node_id"), Str(pull, "body"), Time(Str(pull, "updated_at"))),
            "PullRequestReviewEvent" => ("pull_request_review", Str(review, "node_id"), Str(review, "body"), Time(Str(review, "submitted_at"))),
            "PullRequestReviewCommentEvent" => ("pull_request_review_comment", Str(comment, "node_id"), Str(comment, "body"), Time(Str(comment, "updated_at"))),
            "IssueCommentEvent" => ("issue_comment", Str(comment, "node_id"), Str(comment, "body"), Time(Str(comment, "updated_at"))),
            "IssuesEvent" => ("issues", Str(issue, "node_id"), Str(issue, "body"), Time(Str(issue, "updated_at"))),
            _ => ("push", null, null, null),
        };

        var headSha = type switch
        {
            "PushEvent" => Str(payload, "head"),
            "PullRequestReviewEvent" => Str(review, "commit_id") ?? Str(Obj(pull, "head"), "sha"),
            _ => Str(Obj(pull, "head"), "sha"),
        };

        return new GitHubEvent
        {
            Source = GitHubEventSource.Poll,
            EventName = eventName,
            Action = eventName == "push" ? null : action,
            EventId = id,
            RepoNodeId = repoNodeId,
            PrNodeId = prNodeId,
            IssueNodeId = issueNodeId,
            ObjectNodeId = objectNodeId,
            HeadSha = headSha,
            AuthorLogin = actor,
            AuthorAssociation = association,
            Body = body,
            UpdatedAt = updatedAt ?? createdAt.Value,
            ReviewState = Str(review, "state")?.ToLowerInvariant(),
            ReviewerLogin = Str(Obj(payload, "requested_reviewer"), "login"),
            PrAuthorLogin = Str(Obj(pull, "user"), "login"),
            Merged = type == "PullRequestEvent" && pull is { } p && p.TryGetProperty("merged", out var merged) && merged.ValueKind == JsonValueKind.True,
            Number = prNodeId is null ? null : Int(pull, "number") ?? Int(issue, "number"),
            Title = prNodeId is null ? null : Str(pull, "title") ?? Str(issue, "title"),
            PrState = prNodeId is null ? null : PrStateOf(pull) ?? Str(issue, "state"),
            Draft = Bool(pull, "draft"),
            BaseRef = Str(Obj(pull, "base"), "ref"),
            HeadRef = eventName == "push" ? GitHubBranchRefs.FromPush(Str(payload, "ref"), Str(payload, "head")) : GitHubBranchRefs.SameRepoHeadRef(pull),
            RepoFullName = Str(Obj(e, "repo"), "name"),
            AssigneeLogins = Logins(pull, "assignees").Concat(Logins(pull, "requested_reviewers")).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            IssueNumber = issueNodeId is null ? null : Int(issue, "number"),
            IssueTitle = issueNodeId is null ? null : Str(issue, "title"),
            IssueState = issueNodeId is null ? null : Str(issue, "state")?.ToLowerInvariant(),
            IssueAuthorLogin = issueNodeId is null ? null : Str(Obj(issue, "user"), "login"),
            IssueAssigneeLogins = issueNodeId is null ? [] : Logins(issue, "assignees").Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            AssignedLogin = issueNodeId is null ? null : Str(Obj(payload, "assignee"), "login"),
        };
    }

    private static string? PrStateOf(JsonElement? pull) =>
        Str(pull, "state") is { } state ? (Bool(pull, "merged") == true ? "merged" : state) : null;

    private static int? Int(JsonElement? parent, string name) =>
        parent is { ValueKind: JsonValueKind.Object } p && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    private static bool? Bool(JsonElement? parent, string name) =>
        parent is { ValueKind: JsonValueKind.Object } p && p.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    private static IEnumerable<string> Logins(JsonElement? parent, string name)
    {
        if (parent is not { ValueKind: JsonValueKind.Object } p || !p.TryGetProperty(name, out var list) || list.ValueKind != JsonValueKind.Array)
            yield break;
        foreach (var item in list.EnumerateArray())
            if (Str(item, "login") is { Length: > 0 } login)
                yield return login;
    }

    private static JsonElement? Obj(JsonElement? parent, string name) =>
        parent is { ValueKind: JsonValueKind.Object } p && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    private static string? Str(JsonElement? parent, string name) =>
        parent is { ValueKind: JsonValueKind.Object } p && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static DateTimeOffset? Time(string? text) =>
        text is not null && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t.ToUniversalTime() : null;
}
