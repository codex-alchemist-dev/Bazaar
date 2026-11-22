using Bazaar.Contracts.Git;
using Bazaar.Server.Git.Merge;

namespace Bazaar.Server.Git.Tests.Merge;

public sealed class GitMergeArgsTests
{
    [Fact]
    public void The_server_hash_is_the_shared_contract_hash_the_client_signs()
    {
        var pr = new PrRef("github", "acme/widgets", 7);
        var merge = new MergeRequest(new string('A', 40), "Title (#7)", "Signed-off-by: O <o@example.test>");

        Assert.Equal(
            GitMergeArgsHash.Compute("github", "acme/widgets", 7, new string('a', 40), "Title (#7)", "Signed-off-by: O <o@example.test>"),
            GitMergeArgs.Hash(pr, merge));
        Assert.Equal(GitMergeArgsHash.Action, GitMergeArgs.Action);
    }

    [Fact]
    public void A_changed_field_changes_the_hash()
    {
        var a = GitMergeArgsHash.Compute("github", "acme/widgets", 7, new string('a', 40), "T", "S");
        Assert.NotEqual(a, GitMergeArgsHash.Compute("github", "acme/widgets", 8, new string('a', 40), "T", "S"));
        Assert.NotEqual(a, GitMergeArgsHash.Compute("github", "acme/widgets", 7, new string('a', 40), "T2", "S"));
    }
}
