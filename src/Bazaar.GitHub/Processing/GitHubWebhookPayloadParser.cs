using System.Globalization;
using System.Text.Json;
using Bazaar.GitHub.Events;

namespace Bazaar.GitHub.Processing;

/// <summary>Outcome of parsing one stored webhook body.</summary>
public abstract record WebhookParseResult
{
    /// <summary>A supported event.</summary>
    public sealed record Parsed(GitHubEvent Event) : WebhookParseResult;

    /// <summary>An event type the mapping table does not use (for example <c>ping</c>). Not an error.</summary>
    public sealed record Ignored : WebhookParseResult;

    /// <summary>The body is unusable and never will be; the row is a poison row.</summary>
    public sealed record Malformed(string Reason) : WebhookParseResult;
}

/// <summary>
/// Turns a stored webhook body (<c>github_delivery.payload</c> plus <c>event_type</c>) into a <see cref="GitHubEvent"/> (doc 21, Event mapping).
/// Webhook bodies differ from the Events API shape read by <see cref="GitHubEventsApiParser"/>: the event name is a header,
/// the actor is <c>sender</c>, and the repository sits at the top level. Pure: no I/O.
/// Two limits, both from GitHub's payloads: <c>check_run</c>, <c>check_suite</c> and <c>push</c> carry no PR node id, so they have none here
/// (they carry the listed PR numbers and the head SHA, and <see cref="Bazaar.GitHub.Pr.GitHubPrResolver"/> finds the PR from those);
/// and <c>author_association</c> describes the subject's author, so it is trusted only when the sender is that author, or a known member login.
/// </summary>
public static class GitHubWebhookPayloadParser
{
    private static readonly HashSet<string> _supported = new(StringComparer.Ordinal)
    {
        "pull_request", "pull_request_review", "pull_request_review_comment", "issue_comment", "issues", "check_run", "check_suite", "push",
    };

    public static WebhookParseResult Parse(string eventType, string deliveryId, string? payloadJson, DateTimeOffset receivedAt, IReadOnlySet<string> memberLogins)
    {
        ArgumentNullException.ThrowIfNull(memberLogins);

        if (!_supported.Contains(eventType))
        {
            return new WebhookParseResult.Ignored();
        }

        if (string.IsNullOrEmpty(deliveryId))
        {
            return new WebhookParseResult.Malformed("no delivery id");
        }

        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return new WebhookParseResult.Malformed("no stored payload");
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(payloadJson);
        }
        catch (JsonException)
        {
            return new WebhookParseResult.Malformed("payload is not JSON");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new WebhookParseResult.Malformed("payload is not an object");
            }

            var repoNodeId = Str(Obj(root, "repository"), "node_id");
            var sender = Str(Obj(root, "sender"), "login");
            if (string.IsNullOrEmpty(repoNodeId) || string.IsNullOrEmpty(sender))
            {
                return new WebhookParseResult.Malformed("missing repository.node_id or sender.login");
            }

            var action = Str(root, "action");
            var pull = Obj(root, "pull_request");
            var issue = Obj(root, "issue");
            var comment = Obj(root, "comment");
            var review = Obj(root, "review");
            var checkRun = Obj(root, "check_run");
            var checkSuite = Obj(root, "check_suite");

            JsonElement? subject = eventType switch
            {
                "pull_request" => pull,
                "pull_request_review" => review,
                "pull_request_review_comment" or "issue_comment" => comment,
                "issues" => issue,
                _ => null,
            };

            if (subject is null && eventType is not ("check_run" or "check_suite" or "push"))
            {
                return new WebhookParseResult.Malformed($"{eventType} payload has no subject object");
            }

            var association = "NONE";
            if (memberLogins.Contains(sender))
            {
                association = "MEMBER";
            }
            else if (string.Equals(Str(Obj(subject, "user"), "login"), sender, StringComparison.OrdinalIgnoreCase)
                && Str(subject, "author_association") is { Length: > 0 } declared)
            {
                association = declared;
            }

            var isPrIssue = issue is { } i && i.TryGetProperty("pull_request", out _);
            var prNodeId = eventType switch
            {
                "pull_request" or "pull_request_review" or "pull_request_review_comment" => Str(pull, "node_id"),
                "issue_comment" when isPrIssue => Str(issue, "node_id"),
                _ => null,
            };
            var issueNodeId = eventType is "issues" or "issue_comment" && !isPrIssue ? Str(issue, "node_id") : null;

            var (objectNodeId, body, stamp, headSha, conclusion) = eventType switch
            {
                "pull_request" => (Str(pull, "node_id"), Str(pull, "body"), Time(Str(pull, "updated_at")), Str(Obj(pull, "head"), "sha"), (string?)null),
                "pull_request_review" => (Str(review, "node_id"), Str(review, "body"), Time(Str(review, "submitted_at")), Str(review, "commit_id") ?? Str(Obj(pull, "head"), "sha"), null),
                "pull_request_review_comment" => (Str(comment, "node_id"), Str(comment, "body"), Time(Str(comment, "updated_at")), Str(Obj(pull, "head"), "sha"), null),
                "issue_comment" => (Str(comment, "node_id"), Str(comment, "body"), Time(Str(comment, "updated_at")), null, null),
                "issues" => (Str(issue, "node_id"), Str(issue, "body"), Time(Str(issue, "updated_at")), null, null),
                "check_run" => (Str(checkRun, "node_id"), null, Time(Str(checkRun, "completed_at")) ?? Time(Str(checkRun, "started_at")), Str(checkRun, "head_sha"), Str(checkRun, "conclusion")),
                "check_suite" => (Str(checkSuite, "node_id"), null, Time(Str(checkSuite, "updated_at")), Str(checkSuite, "head_sha"), Str(checkSuite, "conclusion")),
                _ => ((string?)null, null, (DateTimeOffset?)null, PushHead(Str(root, "after")), null),
            };

            if (eventType is "pull_request" or "pull_request_review" or "pull_request_review_comment" && prNodeId is null)
            {
                return new WebhookParseResult.Malformed($"{eventType} payload has no pull_request.node_id");
            }

            return new WebhookParseResult.Parsed(new GitHubEvent
            {
                Source = GitHubEventSource.Webhook,
                EventName = eventType,
                Action = eventType == "push" ? null : action,
                EventId = deliveryId,
                RepoNodeId = repoNodeId,
                PrNodeId = prNodeId,
                IssueNodeId = issueNodeId,
                ObjectNodeId = objectNodeId,
                HeadSha = headSha,
                AuthorLogin = sender,
                AuthorAssociation = association,
                Body = body,
                UpdatedAt = stamp ?? receivedAt,
                ReviewState = Str(review, "state")?.ToLowerInvariant(),
                Conclusion = conclusion,
                ReviewerLogin = Str(Obj(root, "requested_reviewer"), "login"),
                PrAuthorLogin = Str(Obj(pull, "user"), "login"),
                Merged = eventType == "pull_request" && pull is { } p && p.TryGetProperty("merged", out var merged) && merged.ValueKind == JsonValueKind.True,
                Number = prNodeId is null ? null : Int(pull, "number") ?? Int(issue, "number"),
                Title = prNodeId is null ? null : Str(pull, "title") ?? Str(issue, "title"),
                PrState = prNodeId is null ? null : PrStateOf(pull) ?? Str(issue, "state"),
                Draft = Bool(pull, "draft"),
                BaseRef = Str(Obj(pull, "base"), "ref"),
                HeadRef = eventType == "push" ? GitHubBranchRefs.FromPush(Str(root, "ref"), Str(root, "after")) : GitHubBranchRefs.SameRepoHeadRef(pull),
                RepoFullName = Str(Obj(root, "repository"), "full_name"),
                PullRequestNumbers = eventType switch
                {
                    "check_run" => Numbers(checkRun),
                    "check_suite" => Numbers(checkSuite),
                    _ => [],
                },
                AssigneeLogins = Logins(pull, "assignees").Concat(Logins(pull, "requested_reviewers")).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                IssueNumber = issueNodeId is null ? null : Int(issue, "number"),
                IssueTitle = issueNodeId is null ? null : Str(issue, "title"),
                IssueState = issueNodeId is null ? null : Str(issue, "state")?.ToLowerInvariant(),
                IssueAuthorLogin = issueNodeId is null ? null : Str(Obj(issue, "user"), "login"),
                IssueAssigneeLogins = issueNodeId is null ? [] : Logins(issue, "assignees").Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                AssignedLogin = issueNodeId is null ? null : Str(Obj(root, "assignee"), "login"),
            });
        }
    }

    // A push that deletes the branch has an all-zero "after", which names no commit.
    private static string? PushHead(string? after) =>
        string.IsNullOrEmpty(after) || after.All(static c => c == '0') ? null : after;

    private static List<int> Numbers(JsonElement? checkObject)
    {
        var numbers = new List<int>();
        if (checkObject is { ValueKind: JsonValueKind.Object } c && c.TryGetProperty("pull_requests", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                if (Int(item, "number") is { } n && !numbers.Contains(n))
                {
                    numbers.Add(n);
                }
            }
        }

        return numbers;
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
        {
            yield break;
        }

        foreach (var item in list.EnumerateArray())
        {
            if (Str(item, "login") is { Length: > 0 } login)
            {
                yield return login;
            }
        }
    }

    private static JsonElement? Obj(JsonElement? parent, string name) =>
        parent is { ValueKind: JsonValueKind.Object } p && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    private static string? Str(JsonElement? parent, string name) =>
        parent is { ValueKind: JsonValueKind.Object } p && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static DateTimeOffset? Time(string? text) =>
        text is not null && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t.ToUniversalTime() : null;
}
