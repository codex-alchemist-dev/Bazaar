using System.Globalization;
using System.Text.Json;

namespace Bazaar.Server.Git.GitLab;

/// <summary>Maps GitLab REST v4 JSON to the neutral contract records (doc 52). Pure functions, no I/O.</summary>
internal static class GitLabMapping
{
    public static PrSummary ToPrSummary(JsonElement mr, string fullName)
    {
        var iid = RequiredLong(mr, "iid");
        var draft = OptionalBool(mr, "draft") ?? OptionalBool(mr, "work_in_progress") ?? false;
        return new PrSummary(
            new PrRef(GitLabApiClient.ProviderIdValue, fullName, iid),
            RequiredString(mr, "title"),
            NestedString(mr, "author", "username") ?? string.Empty,
            ToPrState(RequiredString(mr, "state")),
            draft,
            OptionalString(mr, "sha") ?? string.Empty,
            new BranchName(RequiredString(mr, "source_branch")),
            new BranchName(RequiredString(mr, "target_branch")),
            ParseTime(RequiredString(mr, "updated_at")));
    }

    /// <summary>
    /// The merge request detail. GitLab's detail has no line totals and no review list in this response, so the additions and
    /// deletions are zero and the reviews are empty. <c>changes_count</c> is the changed-file count, and <c>1000+</c> reads as 1000.
    /// </summary>
    public static PrDetail ToPrDetail(JsonElement mr, string fullName) =>
        new(ToPrSummary(mr, fullName), OptionalString(mr, "description") ?? string.Empty, ParseChangesCount(mr), 0, 0, []);

    public static PrState ToPrState(string state) => state switch
    {
        "opened" or "locked" => PrState.Open,
        "merged" => PrState.Merged,
        "closed" => PrState.Closed,
        _ => throw new GitLabApiException(null, "GitLab merge request state is not recognised."),
    };

    /// <summary>GitLab pipeline job states. Created and running jobs are pending; manual, canceled and skipped jobs are skipped.</summary>
    public static CheckState ToCheckState(string status) => status switch
    {
        "success" => CheckState.Success,
        "failed" => CheckState.Failure,
        "skipped" or "canceled" or "manual" => CheckState.Skipped,
        _ => CheckState.Pending,
    };

    /// <summary>One entry of <c>merge_requests/:iid/diffs</c>. Line counts come from the diff text, which has no file headers.</summary>
    public static PrFile ToPrFile(JsonElement diff)
    {
        var path = OptionalString(diff, "new_path");
        if (string.IsNullOrEmpty(path))
        {
            path = RequiredString(diff, "old_path");
        }

        var status = OptionalBool(diff, "new_file") == true ? "added"
            : OptionalBool(diff, "deleted_file") == true ? "removed"
            : OptionalBool(diff, "renamed_file") == true ? "renamed"
            : "modified";

        var text = OptionalString(diff, "diff") ?? string.Empty;
        var truncated = OptionalBool(diff, "collapsed") == true || OptionalBool(diff, "too_large") == true;
        var (additions, deletions) = CountLines(text);
        return new PrFile(path, status, additions, deletions, truncated ? null : text, truncated);
    }

    public static (int Additions, int Deletions) CountLines(string diff)
    {
        var additions = 0;
        var deletions = 0;
        foreach (var line in diff.Split('\n'))
        {
            if (line.StartsWith('+'))
            {
                additions++;
            }
            else if (line.StartsWith('-'))
            {
                deletions++;
            }
        }

        return (additions, deletions);
    }

    public static string RequiredString(JsonElement element, string name)
    {
        var value = OptionalString(element, name);
        return value ?? throw Missing(name);
    }

    public static string? OptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public static long RequiredLong(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number
            : throw Missing(name);

    public static bool? OptionalBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.ValueKind == JsonValueKind.True
            : null;

    public static string? NestedString(JsonElement element, string outer, string inner) =>
        element.TryGetProperty(outer, out var child) && child.ValueKind == JsonValueKind.Object ? OptionalString(child, inner) : null;

    /// <summary>A JSON array, or a typed error when the response is another shape.</summary>
    public static JsonElement RequireArray(JsonDocument doc, string what) =>
        doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement
            : throw new GitLabApiException(null, $"GitLab {what} response was not an array.");

    private static int ParseChangesCount(JsonElement mr)
    {
        if (!mr.TryGetProperty("changes_count", out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return 0;
        }

        var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
        return int.TryParse(text?.TrimEnd('+'), NumberStyles.None, CultureInfo.InvariantCulture, out var count) ? count : 0;
    }

    private static DateTimeOffset ParseTime(string text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time)
            ? time
            : throw new GitLabApiException(null, "GitLab merge request timestamp was not recognised.");

    private static GitLabApiException Missing(string name) =>
        new(null, $"GitLab response is missing '{name}'.");
}
