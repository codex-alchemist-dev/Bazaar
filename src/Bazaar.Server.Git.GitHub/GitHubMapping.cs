using System.Globalization;
using System.Text.Json;

namespace Bazaar.Server.Git.GitHub;

/// <summary>Maps GitHub REST JSON to the neutral contract records (doc 52). Pure functions, no I/O.</summary>
internal static class GitHubMapping
{
    public static PrSummary ToPrSummary(JsonElement pr, string fullName) =>
        new(
            new PrRef(GitHubApiClient.ProviderIdValue, fullName, RequiredLong(pr, "number")),
            RequiredString(pr, "title"),
            NestedString(pr, "user", "login") ?? string.Empty,
            ToPrState(pr),
            OptionalBool(pr, "draft") ?? false,
            NestedString(pr, "head", "sha") ?? string.Empty,
            new BranchName(NestedString(pr, "head", "ref") ?? throw Missing("head.ref")),
            new BranchName(NestedString(pr, "base", "ref") ?? throw Missing("base.ref")),
            ParseTime(RequiredString(pr, "updated_at")));

    /// <summary>The pull request detail. <paramref name="reviews"/> come from the separate reviews call.</summary>
    public static PrDetail ToPrDetail(JsonElement pr, string fullName, IReadOnlyList<ReviewSummary> reviews) =>
        new(
            ToPrSummary(pr, fullName),
            OptionalString(pr, "body") ?? string.Empty,
            (int)(OptionalLong(pr, "changed_files") ?? 0),
            (int)(OptionalLong(pr, "additions") ?? 0),
            (int)(OptionalLong(pr, "deletions") ?? 0),
            reviews);

    /// <summary>A merged pull request is closed with a merge time. List responses carry <c>merged_at</c>, detail also <c>merged</c>.</summary>
    public static PrState ToPrState(JsonElement pr) => RequiredString(pr, "state") switch
    {
        "open" => PrState.Open,
        "closed" => OptionalBool(pr, "merged") == true || OptionalString(pr, "merged_at") is not null ? PrState.Merged : PrState.Closed,
        _ => throw new GitHubApiException(null, "GitHub pull request state is not recognised."),
    };

    /// <summary>One review. Dismissed and pending reviews are not verdicts and map to null.</summary>
    public static ReviewSummary? ToReview(JsonElement review)
    {
        var verdict = OptionalString(review, "state") switch
        {
            "APPROVED" => (ReviewEvent?)ReviewEvent.Approve,
            "CHANGES_REQUESTED" => ReviewEvent.RequestChanges,
            "COMMENTED" => ReviewEvent.Comment,
            _ => null,
        };
        if (verdict is not { } v || OptionalString(review, "submitted_at") is not { } submitted)
        {
            return null;
        }

        return new ReviewSummary(NestedString(review, "user", "login") ?? string.Empty, v, OptionalString(review, "commit_id") ?? string.Empty, ParseTime(submitted));
    }

    /// <summary>
    /// A check run. Anything not completed is pending. Neutral and skipped runs are skipped. Every other conclusion, including
    /// cancelled, timed out and action required, is a failure, so a gate never reads an unfinished or aborted run as green.
    /// </summary>
    public static CheckState ToCheckState(string? status, string? conclusion)
    {
        if (status != "completed")
        {
            return CheckState.Pending;
        }

        return conclusion switch
        {
            "success" => CheckState.Success,
            "neutral" or "skipped" => CheckState.Skipped,
            _ => CheckState.Failure,
        };
    }

    /// <summary>A commit status context: <c>success</c>, <c>pending</c>, <c>failure</c> or <c>error</c>.</summary>
    public static CheckState ToStatusState(string? state) => state switch
    {
        "success" => CheckState.Success,
        "failure" or "error" => CheckState.Failure,
        _ => CheckState.Pending,
    };

    /// <summary>One entry of <c>pulls/:n/files</c>. GitHub omits <c>patch</c> for binary and very large diffs, which reads as truncated.</summary>
    public static PrFile ToPrFile(JsonElement file)
    {
        var patch = OptionalString(file, "patch");
        var additions = (int)(OptionalLong(file, "additions") ?? 0);
        var deletions = (int)(OptionalLong(file, "deletions") ?? 0);
        var status = RequiredString(file, "status");
        return new PrFile(RequiredString(file, "filename"), status, additions, deletions, patch, patch is null && additions + deletions > 0);
    }

    public static string RequiredString(JsonElement element, string name) =>
        OptionalString(element, name) ?? throw Missing(name);

    public static string? OptionalString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static long RequiredLong(JsonElement element, string name) =>
        OptionalLong(element, name) ?? throw Missing(name);

    public static long? OptionalLong(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number
            : null;

    public static bool? OptionalBool(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.ValueKind == JsonValueKind.True
            : null;

    public static string? NestedString(JsonElement element, string outer, string inner) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(outer, out var child) && child.ValueKind == JsonValueKind.Object
            ? OptionalString(child, inner)
            : null;

    /// <summary>A JSON array, or a typed error when the response is another shape.</summary>
    public static JsonElement RequireArray(JsonElement element, string what) =>
        element.ValueKind == JsonValueKind.Array
            ? element
            : throw new GitHubApiException(null, $"GitHub {what} response was not an array.");

    /// <summary>A named array property of a response object (for example <c>check_runs</c>), or a typed error.</summary>
    public static JsonElement RequireArrayProperty(JsonElement element, string name, string what) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value
            : throw new GitHubApiException(null, $"GitHub {what} response is missing '{name}'.");

    private static DateTimeOffset ParseTime(string text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time)
            ? time
            : throw new GitHubApiException(null, "GitHub timestamp was not recognised.");

    private static GitHubApiException Missing(string name) =>
        new(null, $"GitHub response is missing '{name}'.");
}
