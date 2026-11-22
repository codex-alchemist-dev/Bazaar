namespace Bazaar.ModulePermissions.Tests;

public class ModuleTargetRulesTests
{
    [Theory]
    [InlineData("gc")]
    [InlineData("vault/notes")]
    [InlineData("/data/modules/weather")]
    [InlineData("C:/Data/x")]
    [InlineData("a/b/")]
    [InlineData("file.name.txt")]
    [InlineData("...a")]
    public void Clean_targets_pass(string target) => Assert.True(ModuleTargetRules.IsValidTarget(target));

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("a/../b")]
    [InlineData("a/./b")]
    [InlineData("a//b")]
    [InlineData("//a")]
    [InlineData("a/.. /b")]
    [InlineData("a/ /b")]
    [InlineData("a\\b")]
    [InlineData("a\\..\\b")]
    [InlineData("a/*")]
    [InlineData("*")]
    [InlineData("a\0b")]
    [InlineData("a\nb")]
    [InlineData("a\u202Eb")]
    [InlineData("a\u200Bb")]
    [InlineData("a\u2028b")]
    [InlineData("a\uE000b")]
    [InlineData("a\uFFFDb")]
    public void Traversal_hidden_characters_and_wildcards_fail(string target) => Assert.False(ModuleTargetRules.IsValidTarget(target));

    [Fact]
    public void A_lone_surrogate_fails() => Assert.False(ModuleTargetRules.IsValidTarget(string.Concat("a", ((char)0xD800).ToString(), "b")));

    [Fact]
    public void A_target_over_the_length_limit_fails() =>
        Assert.False(ModuleTargetRules.IsValidTarget(new string('a', ModuleTargetRules.MaxTargetLength + 1)));

    [Theory]
    [InlineData("a/b/*", "a/b/c", true)]
    [InlineData("a/b/*", "a/b/c/d", true)]
    [InlineData("a/b/*", "a/b", false)]
    [InlineData("a/b/*", "a/b/", false)]
    [InlineData("a/b/*", "a/bc", false)]
    [InlineData("a/b/*", "a/bc/d", false)]
    [InlineData("a/b/*", "A/b/c", false)]
    [InlineData("a/b/*", "a/b/../c", false)]
    [InlineData("a/b/*", "x/a/b/c", false)]
    [InlineData("a/b", "a/b", true)]
    [InlineData("a/b", "a/b/c", false)]
    [InlineData("a/b", "a/b/", false)]
    [InlineData("a/b", "a/B", false)]
    [InlineData("*", "a", false)]
    [InlineData("/*", "a", false)]
    [InlineData("a/*/*", "a/b/c", false)]
    [InlineData("a*", "ab", false)]
    public void Scopes_cover_by_whole_segments_only(string scope, string target, bool expected) =>
        Assert.Equal(expected, ModuleTargetRules.ScopeCovers(scope, target));

    [Theory]
    [InlineData("example.com", "example.com")]
    [InlineData("API.Example.COM", "api.example.com")]
    [InlineData("10.0.0.1", "10.0.0.1")]
    [InlineData("::1", "::1")]
    [InlineData("2001:db8::1", "2001:db8::1")]
    [InlineData("xn--bcher-kva.example", "xn--bcher-kva.example")]
    public void Hosts_have_one_spelling(string host, string expected) => Assert.Equal(expected, ModuleTargetRules.NormalizeHost(host));

    [Theory]
    [InlineData("")]
    [InlineData("example.com.")]
    [InlineData("example.com:443")]
    [InlineData("user@example.com")]
    [InlineData("[::1]")]
    [InlineData("fe80::1%eth0")]
    [InlineData("0:0:0:0:0:0:0:1")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("010.0.0.1")]
    [InlineData("10.0.1")]
    [InlineData("2130706433")]
    [InlineData("0x7f000001")]
    [InlineData("0x7f.0.0.1")]
    [InlineData("127.1")]
    [InlineData("exa mple.com")]
    [InlineData("bücher.example")]
    [InlineData("еxample.com")]
    [InlineData("-a.example")]
    [InlineData("a..example")]
    [InlineData("example.com/path")]
    [InlineData("*.example.com")]
    public void Ambiguous_host_spellings_have_none(string host) => Assert.Null(ModuleTargetRules.NormalizeHost(host));
}
