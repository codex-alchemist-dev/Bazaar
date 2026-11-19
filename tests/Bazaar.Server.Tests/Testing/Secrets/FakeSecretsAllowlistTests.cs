using System.Text.RegularExpressions;
using Bazaar.Testing.Secrets;

namespace Bazaar.Server.Tests.Testing.Secrets;

/// <summary>Keeps <c>.gitleaks.toml</c> and <see cref="FakeSecrets"/> in step: exact-value allowlist, nothing broader.</summary>
public class FakeSecretsAllowlistTests
{
    private static readonly string[] _allowlistRegexes = LoadRegexes();

    public static TheoryData<string> Names() => new(FakeSecrets.All.Keys);

    [Theory]
    [MemberData(nameof(Names))]
    public void Every_fake_secret_has_one_anchored_exact_value_regex(string name)
    {
        var value = FakeSecrets.All[name];

        var matching = _allowlistRegexes.Where(r => Regex.IsMatch(value, r)).ToArray();

        var regex = Assert.Single(matching);
        Assert.Equal($"^{value}$", regex);
    }

    [Fact]
    public void Allowlist_regexes_are_exactly_the_fake_secrets()
    {
        var expected = FakeSecrets.All.Values.Select(v => $"^{v}$").Order(StringComparer.Ordinal);

        Assert.Equal(expected, _allowlistRegexes.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Fake_values_are_plain_literals_so_the_regex_is_exact()
    {
        Assert.All(FakeSecrets.All.Values, v => Assert.Matches("^[A-Za-z0-9_-]+$", v));
    }

    private static string[] LoadRegexes()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, ".gitleaks.toml")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var toml = File.ReadAllText(Path.Combine(dir.FullName, ".gitleaks.toml"));
        var block = Regex.Match(toml, @"(?ms)^regexes\s*=\s*\[(?<body>.*?)^\]");
        Assert.True(block.Success, "no regexes block in .gitleaks.toml");
        return Regex.Matches(block.Groups["body"].Value, @"'''(?<r>[^']+)'''").Select(m => m.Groups["r"].Value).ToArray();
    }
}
