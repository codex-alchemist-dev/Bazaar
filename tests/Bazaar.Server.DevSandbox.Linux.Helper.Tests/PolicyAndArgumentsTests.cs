namespace Bazaar.Server.DevSandbox.Linux.Helper.Tests;

public sealed class PolicyAndArgumentsTests
{
    private const string _valid = "{\"version\":1,\"readPaths\":[\"/usr/bin\",\"/lib\"],\"writePaths\":[\"/tmp/work\"],\"extraSyscalls\":[\"memfd_create\"],\"path\":\"/usr/bin:/bin\",\"addressSpaceBytes\":268435456}";

    [Fact]
    public void A_valid_policy_parses_with_every_field()
    {
        var policy = HelperPolicyParser.Parse(_valid, out var error);

        Assert.Null(error);
        Assert.NotNull(policy);
        Assert.Equal(["/usr/bin", "/lib"], policy.ReadPaths);
        Assert.Equal(["/tmp/work"], policy.WritePaths);
        Assert.Equal(["memfd_create"], policy.ExtraSyscalls);
        Assert.Equal("/usr/bin:/bin", policy.Path);
        Assert.Equal(268435456L, policy.AddressSpaceBytes);
    }

    [Fact]
    public void Optional_fields_default_to_empty_lists_and_no_address_limit()
    {
        var policy = HelperPolicyParser.Parse("{\"version\":1,\"writePaths\":[\"/w\"]}", out var error);

        Assert.Null(error);
        Assert.NotNull(policy);
        Assert.Empty(policy.ReadPaths);
        Assert.Empty(policy.ExtraSyscalls);
        Assert.Null(policy.AddressSpaceBytes);
    }

    [Theory]
    [InlineData("{\"version\":2,\"writePaths\":[\"/w\"]}", "version")]
    [InlineData("{\"version\":1}", "writePaths")]
    [InlineData("{\"version\":1,\"writePaths\":[\"/\"]}", "must not contain /")]
    [InlineData("{\"version\":1,\"writePaths\":[\"relative\"]}", "absolute")]
    [InlineData("{\"version\":1,\"writePaths\":[\"/w\"],\"readPaths\":[\"lib\"]}", "absolute")]
    [InlineData("{\"version\":1,\"writePaths\":[\"/w\"],\"extraSyscalls\":[\"socket\"]}", "always denied")]
    [InlineData("{\"version\":1,\"writePaths\":[\"/w\"],\"extraSyscalls\":[\"ptrace\"]}", "always denied")]
    [InlineData("{\"version\":1,\"writePaths\":[\"/w\"],\"extraSyscalls\":[\"no_such_call\"]}", "catalogue")]
    [InlineData("{\"version\":1,\"writePaths\":[\"/w\"],\"extraSyscalls\":[\"read\"]}", "catalogue")]
    [InlineData("{\"version\":1,\"writePaths\":[\"/w\"],\"addressSpaceBytes\":0}", "addressSpaceBytes")]
    [InlineData("{\"version\":1,\"writePaths\":[\"/w\"],\"path\":\"\"}", "path")]
    public void An_invalid_policy_is_rejected_with_a_reason(string json, string expectedFragment)
    {
        var policy = HelperPolicyParser.Parse(json, out var error);

        Assert.Null(policy);
        Assert.NotNull(error);
        Assert.Contains(expectedFragment, error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("not json")]
    [InlineData("{\"version\":1,\"writePaths\":[\"/w\"],\"allowEverything\":true}")]
    [InlineData("{\"version\":\"1\",\"writePaths\":[\"/w\"]}")]
    public void Malformed_json_and_unknown_properties_are_rejected_not_ignored(string json)
    {
        Assert.Null(HelperPolicyParser.Parse(json, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Property_names_are_case_sensitive()
    {
        Assert.Null(HelperPolicyParser.Parse("{\"Version\":1,\"writePaths\":[\"/w\"]}", out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Arguments_parse_policy_options_and_a_target_with_its_arguments()
    {
        var a = new HelperArguments();
        var error = a.Parse(["--policy", "/p.json", "--delete-policy-after-read", "--", "/bin/cat", "-n", "/etc/x"]);

        Assert.Null(error);
        Assert.Equal("/p.json", a.PolicyPath);
        Assert.True(a.DeletePolicyAfterRead);
        Assert.Equal("/bin/cat", a.Target);
        Assert.Equal(["-n", "/etc/x"], a.TargetArguments);
    }

    [Fact]
    public void Arguments_after_the_separator_are_never_interpreted_as_helper_options()
    {
        var a = new HelperArguments();
        var error = a.Parse(["--policy", "/p.json", "--", "/bin/echo", "--policy", "--info", "--self-test"]);

        Assert.Null(error);
        Assert.False(a.Info);
        Assert.Null(a.SelfTest);
        Assert.Equal(["--policy", "--info", "--self-test"], a.TargetArguments);
    }

    [Theory]
    [InlineData("--policy", "/p.json")]
    [InlineData("--", "/bin/true")]
    [InlineData("--policy", "/p.json", "--", "relative/true")]
    [InlineData("--policy", "/p.json", "--bogus", "--", "/bin/true")]
    [InlineData("--policy")]
    [InlineData("--policy", "/p.json", "--self-test", "mount")]
    public void Bad_command_lines_are_usage_errors(params string[] args)
    {
        Assert.NotNull(new HelperArguments().Parse(args));
    }

    [Fact]
    public void Info_needs_no_policy_and_no_target()
    {
        var a = new HelperArguments();
        Assert.Null(a.Parse(["--info"]));
        Assert.True(a.Info);
    }

    [Fact]
    public void Self_test_needs_no_target()
    {
        var a = new HelperArguments();
        Assert.Null(a.Parse(["--policy", "/p.json", "--self-test", "socket"]));
        Assert.Equal("socket", a.SelfTest);
    }
}
