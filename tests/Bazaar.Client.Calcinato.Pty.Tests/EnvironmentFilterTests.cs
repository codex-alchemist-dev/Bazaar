using Bazaar.Client.Calcinato.Pty;

namespace Bazaar.Client.Calcinato.Pty.Tests;

public sealed class EnvironmentFilterTests
{
    [Theory]
    [InlineData("BAZAAR_SERVER", true)]
    [InlineData("bazaar_token_cache", true)]
    [InlineData("GITHUB_TOKEN", true)]
    [InlineData("aws_secret_access_key", true)]
    [InlineData("DB_PASSWORD", true)]
    [InlineData("OPENAI_API_KEY", true)]
    [InlineData("PATH", false)]
    [InlineData("HOME", false)]
    [InlineData("SSH_AUTH_SOCK", false)]
    [InlineData("TERM", false)]
    [InlineData("Bazaarish", false)]
    public void Deny_list_matches_prefix_and_fragments_case_insensitively(string name, bool denied)
    {
        Assert.Equal(denied, EnvironmentFilter.IsDenied(name));
    }

    [Fact]
    public void Build_drops_denied_names_from_inherited_and_spec_and_lets_spec_win()
    {
        var inherited = new Dictionary<string, string>
        {
            ["PATH"] = "/usr/bin",
            ["BAZAAR_INTERNAL"] = "x",
            ["GH_TOKEN"] = "y",
        };
        var spec = new Dictionary<string, string>
        {
            ["FOO"] = "bar",
            ["BAZAAR_Y"] = "z",
            ["PATH"] = "/custom",
        };

        var result = EnvironmentFilter.Build(inherited, spec, StringComparer.Ordinal);

        Assert.Equal(2, result.Count);
        Assert.Equal("/custom", result["PATH"]);
        Assert.Equal("bar", result["FOO"]);
    }

    [Fact]
    public void Build_with_ignore_case_comparer_merges_names_that_differ_only_in_case()
    {
        var inherited = new Dictionary<string, string> { ["Path"] = "C:\\Windows" };
        var spec = new Dictionary<string, string> { ["PATH"] = "C:\\Custom" };

        var result = EnvironmentFilter.Build(inherited, spec, StringComparer.OrdinalIgnoreCase);

        Assert.Single(result);
        Assert.Equal("C:\\Custom", result["PATH"]);
    }

    [Fact]
    public void Windows_environment_block_is_nul_separated_and_double_terminated()
    {
        var block = EnvironmentFilter.WindowsBlock(
            new Dictionary<string, string> { ["A"] = "1", ["B"] = "2" });

        Assert.Equal("A=1\0B=2\0\0", block);
    }
}
