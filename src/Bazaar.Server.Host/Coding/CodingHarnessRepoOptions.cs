using Bazaar.Server.CodingGit;
using Microsoft.Extensions.Configuration;

namespace Bazaar.Server.Host.Coding;

/// <summary>
/// The repository coding tasks work in, until task cards carry their own (doc 17, "Tool dispatch registration"). Read from
/// <c>Coding:Harness:Repo:Id</c> and <c>Coding:Harness:Repo:DefaultBranch</c>. Both are required together; with neither set
/// <see cref="Repo"/> is null and a task-start flow must refuse to open a worktree rather than guess a repository.
/// </summary>
public sealed record CodingHarnessRepoOptions(RepoRef? Repo)
{
    /// <summary>Reads the setting. Throws when only one of the two values is set or a value is blank, so a half-set repository stops the host instead of running tasks nowhere.</summary>
    /// <exception cref="InvalidOperationException">The setting is half-set or blank.</exception>
    public static CodingHarnessRepoOptions From(IConfiguration? configuration)
    {
        var section = configuration?.GetSection("Coding:Harness:Repo");
        var id = section?["Id"];
        var branch = section?["DefaultBranch"];
        if (id is null && branch is null)
        {
            return new CodingHarnessRepoOptions((RepoRef?)null);
        }

        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(branch))
        {
            throw new InvalidOperationException("Coding:Harness:Repo needs both Id and DefaultBranch, and neither may be blank.");
        }

        return new CodingHarnessRepoOptions(new RepoRef(id.Trim(), branch.Trim()));
    }
}
