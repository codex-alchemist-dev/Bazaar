using Bazaar.Contracts.GitHub;

namespace Bazaar.Client.PullRequests;

/// <summary>
/// The four fields that name one PR on GitHub: installation, owner, repo and number. The merge args hash (doc 21, Merge request
/// path) binds these, so a signature for one PR cannot be replayed on another. Built only from server replies that carry them.
/// </summary>
public sealed record PrIdentity(long InstallationId, string Owner, string Repo, int Number)
{
    /// <summary>The identity from a list row, or null when the server did not send the identity fields.</summary>
    public static PrIdentity? From(PrListRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return From(row.InstallationId, row.Owner, row.RepoName, row.Number);
    }

    /// <summary>The identity from a summary reply, or null when the server did not send the identity fields.</summary>
    public static PrIdentity? From(PrSummaryReply reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        return reply.Number is { } number ? From(reply.InstallationId, reply.Owner, reply.RepoName, number) : null;
    }

    private static PrIdentity? From(long? installationId, string? owner, string? repo, int number) =>
        installationId is { } id && !string.IsNullOrWhiteSpace(owner) && !string.IsNullOrWhiteSpace(repo)
            ? new PrIdentity(id, owner, repo, number)
            : null;
}
