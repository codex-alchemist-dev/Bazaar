namespace Bazaar.Client.GitGui.Gitlab;

/// <summary>
/// The GitLab presentation of a merge request: wording, state labels and site links (doc 52, Client). It never calls GitLab
/// and never holds a credential.
/// </summary>
public sealed class GitLabProviderUi : IGitProviderUi
{
    private readonly string _baseUrl;

    /// <summary>Builds the UI for gitlab.com, or for a self-managed instance when <paramref name="options"/> sets one.</summary>
    /// <exception cref="ArgumentException">The base URL is not an acceptable https origin.</exception>
    public GitLabProviderUi(GitLabProviderUiOptions? options = null)
    {
        _baseUrl = GitLabLinkRules.NormaliseBaseUrl((options ?? new GitLabProviderUiOptions()).BaseUrl);
    }

    /// <inheritdoc />
    public string ProviderId => "gitlab";

    /// <inheritdoc />
    public string DisplayName => "GitLab";

    /// <inheritdoc />
    public string PrNoun => "merge request";

    /// <inheritdoc />
    public string CheckLabel(string state) => state switch
    {
        "pending" => "Running",
        "success" => "Passed",
        "failure" => "Failed",
        "skipped" => "Skipped",
        _ => state,
    };

    /// <inheritdoc />
    public string ReviewLabel(string verdict) => verdict switch
    {
        "comment" => "Commented",
        "requestchanges" => "Changes requested",
        "approve" => "Approved",
        _ => verdict,
    };

    /// <inheritdoc />
    public Uri? PrWebUrl(string repoFullName, long number) => GitLabLinkRules.MergeRequestPage(_baseUrl, repoFullName, number);

    /// <inheritdoc />
    public Uri? CommitWebUrl(string repoFullName, string sha) => GitLabLinkRules.CommitPage(_baseUrl, repoFullName, sha);
}
