using System.Text.Json;
using Bazaar.Contracts.Git;

namespace Bazaar.Contracts.Tests.Git;

public sealed class GitRepoDtoTests
{
    [Fact]
    public void Round_trips_through_the_source_generated_context()
    {
        GitRepoDto[] repos = [new("github", "acme/widgets", "main"), new("gitlab", "g/sub/p", null)];

        var json = JsonSerializer.SerializeToUtf8Bytes(repos, GitReadContractsJsonContext.Default.GitRepoDtoArray);
        var back = JsonSerializer.Deserialize(json, GitReadContractsJsonContext.Default.GitRepoDtoArray);

        Assert.Equal(repos, back);
    }

    [Fact]
    public void Uses_camel_case_and_omits_a_null_default_branch()
    {
        var json = JsonSerializer.Serialize([new GitRepoDto("github", "acme/widgets", null)], GitReadContractsJsonContext.Default.GitRepoDtoArray);

        Assert.Equal("""[{"provider":"github","fullName":"acme/widgets"}]""", json);
    }
}
