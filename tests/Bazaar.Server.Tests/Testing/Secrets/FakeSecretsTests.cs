using Bazaar.Testing.Secrets;

namespace Bazaar.Server.Tests.Testing.Secrets;

public class FakeSecretsTests
{
    [Fact]
    public void No_fake_value_appears_whole_in_its_own_source_file()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "Bazaar.Testing", "Secrets", "FakeSecrets.cs")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var source = File.ReadAllText(Path.Combine(dir.FullName, "src", "Bazaar.Testing", "Secrets", "FakeSecrets.cs"));

        Assert.All(FakeSecrets.All.Values, v => Assert.DoesNotContain(v, source, StringComparison.Ordinal));
    }

    [Fact]
    public void Shapes_have_the_expected_prefixes_and_lengths()
    {
        Assert.StartsWith("ghp_", FakeSecrets.GitHubPat);
        Assert.Equal(40, FakeSecrets.GitHubPat.Length);
        Assert.StartsWith("github_pat_", FakeSecrets.GitHubFineGrainedPat);
        Assert.StartsWith("AKIA", FakeSecrets.AwsAccessKeyId);
        Assert.StartsWith("sk-ant-api03-", FakeSecrets.AnthropicApiKey);
        Assert.StartsWith("AIza", FakeSecrets.GoogleApiKey);
    }
}
