namespace Bazaar.Client.GitGui.Github;

/// <summary>
/// The GitHub presentation of a PR: wording, state labels and site links (doc 52, Client). It never calls GitHub and
/// never holds a credential.
/// </summary>
public sealed class GitHubProviderUi : IGitProviderUi
{
    /// <inheritdoc />
    public string ProviderId => "github";

    /// <inheritdoc />
    public string DisplayName => "GitHub";

    /// <inheritdoc />
    public string PrNoun => "pull request";

    /// <inheritdoc />
    public string CheckLabel(string state) => state switch
    {
        "pending" => "Pending",
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
    public Uri? PrWebUrl(string repoFullName, long number) => GitHubLinkRules.PrPage(repoFullName, number);

    /// <inheritdoc />
    public Uri? CommitWebUrl(string repoFullName, string sha) => GitHubLinkRules.CommitPage(repoFullName, sha);
}
