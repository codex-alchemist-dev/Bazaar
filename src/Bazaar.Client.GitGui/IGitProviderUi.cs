namespace Bazaar.Client.GitGui;

/// <summary>
/// Per-provider presentation, supplied by a provider client module (<c>Bazaar.Client.GitGui.Github</c>, <c>.Gitlab</c>; doc 52,
/// Client). It holds wording and mappings only. It never calls a forge and never holds a credential.
/// </summary>
public interface IGitProviderUi
{
    /// <summary>The provider id this UI serves, matching the server's provider id, for example <c>github</c>.</summary>
    string ProviderId { get; }

    /// <summary>Display name, for example <c>GitHub</c>.</summary>
    string DisplayName { get; }

    /// <summary>What this provider calls a PR: <c>pull request</c> or <c>merge request</c>. Lower case.</summary>
    string PrNoun { get; }

    /// <summary>Short label for a check or pipeline state wire value (<c>pending</c>, <c>success</c>, <c>failure</c>, <c>skipped</c>); unknown values pass through.</summary>
    string CheckLabel(string state);

    /// <summary>Short label for a review verdict wire value (<c>comment</c>, <c>requestchanges</c>, <c>approve</c>); unknown values pass through.</summary>
    string ReviewLabel(string verdict);

    /// <summary>The https URL of the PR on the provider's site, for "open in browser". Null when the repo name is not valid for this provider.</summary>
    Uri? PrWebUrl(string repoFullName, long number);

    /// <summary>The https URL of a commit on the provider's site. Null when the repo name is not valid.</summary>
    Uri? CommitWebUrl(string repoFullName, string sha);
}

/// <summary>Resolves a provider UI by id. Unknown ids return null and the panes render neutral labels (doc 52).</summary>
public interface IGitProviderUiRegistry
{
    IGitProviderUi? Find(string providerId);

    IReadOnlyCollection<string> ProviderIds { get; }
}
