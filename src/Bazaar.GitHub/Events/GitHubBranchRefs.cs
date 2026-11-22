using System.Text.Json;

namespace Bazaar.GitHub.Events;

/// <summary>
/// Branch names for the PR lookup (doc 21, Event mapping; PROPOSED). Shared by the webhook and Events API parsers so both fill
/// <see cref="GitHubEvent.HeadRef"/> by the same rules.
/// </summary>
internal static class GitHubBranchRefs
{
    private const string _headsPrefix = "refs/heads/";

    /// <summary>The branch a push updated, from its <c>ref</c>. Null for a tag, a branch-deleting push (all-zero <paramref name="after"/>) or no ref.</summary>
    public static string? FromPush(string? gitRef, string? after)
    {
        if (gitRef is null || !gitRef.StartsWith(_headsPrefix, StringComparison.Ordinal) || gitRef.Length == _headsPrefix.Length)
            return null;
        if (!string.IsNullOrEmpty(after) && after.All(static c => c == '0'))
            return null;
        return gitRef[_headsPrefix.Length..];
    }

    /// <summary>
    /// <c>head.ref</c> of a pull request object, only when the head repository is the base repository. A fork PR returns null,
    /// because the fork's branch name must never match a push to a same-named branch of the base repository.
    /// </summary>
    public static string? SameRepoHeadRef(JsonElement? pull)
    {
        var head = Child(pull, "head");
        var headRepo = Text(Child(head, "repo"), "full_name");
        var baseRepo = Text(Child(Child(pull, "base"), "repo"), "full_name");
        if (headRepo is null || baseRepo is null || !string.Equals(headRepo, baseRepo, StringComparison.OrdinalIgnoreCase))
            return null;
        return Text(head, "ref") is { Length: > 0 } name ? name : null;
    }

    private static JsonElement? Child(JsonElement? parent, string name) =>
        parent is { ValueKind: JsonValueKind.Object } p && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    private static string? Text(JsonElement? parent, string name) =>
        parent is { ValueKind: JsonValueKind.Object } p && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
