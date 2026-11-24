namespace Bazaar.Server.DevSandbox.Linux.Helper.Tests.Adversarial;

public sealed class HelperInputAdversarialTests
{
    private static string? Parse(params string[] args) => new HelperArguments().Parse(args);

    [Theory]
    [InlineData("--policy", "/p.json", "--policy", "/q.json", "--", "/bin/true")]
    [InlineData("--policy", "/p.json", "--delete-policy-after-read", "--delete-policy-after-read", "--", "/bin/true")]
    [InlineData("--policy", "/p.json", "--simulate-landlock-unavailable", "--simulate-landlock-unavailable", "--", "/bin/true")]
    [InlineData("--info", "--info")]
    [InlineData("--policy", "/p.json", "--self-test", "socket", "--self-test", "socket")]
    public void Duplicate_options_are_usage_errors(params string[] args)
    {
        Assert.NotNull(Parse(args));
    }

    [Theory]
    [InlineData("--policy", "--info")]
    [InlineData("--policy", "--delete-policy-after-read", "--", "/bin/true")]
    [InlineData("--policy", "--", "/bin/true")]
    [InlineData("--policy", "--self-test", "socket")]
    public void An_option_value_that_looks_like_an_option_is_a_usage_error(params string[] args)
    {
        Assert.NotNull(Parse(args));
    }

    [Theory]
    [InlineData("/p/../etc/policy.json")]
    [InlineData("/p/./policy.json")]
    [InlineData("policy.json")]
    [InlineData("./policy.json")]
    [InlineData("../policy.json")]
    [InlineData("")]
    public void The_policy_path_must_be_clean_and_absolute(string path)
    {
        Assert.NotNull(Parse("--policy", path, "--", "/bin/true"));
    }

    [Theory]
    [InlineData("/bin/../bin/true")]
    [InlineData("/bin/./true")]
    [InlineData("bin/true")]
    [InlineData("./true")]
    [InlineData("")]
    [InlineData("/bin/true\0")]
    public void The_target_must_be_clean_and_absolute(string target)
    {
        Assert.NotNull(Parse("--policy", "/p.json", "--", target));
    }

    [Theory]
    [InlineData("--self-test")]
    [InlineData("--self-test", "connect")]
    [InlineData("--self-test", "--info")]
    public void Self_test_accepts_only_socket(params string[] tail)
    {
        Assert.NotNull(Parse(["--policy", "/p.json", .. tail]));
    }

    [Theory]
    [InlineData("--Policy", "/p.json", "--", "/bin/true")]
    [InlineData("-policy", "/p.json", "--", "/bin/true")]
    [InlineData("--policy=/p.json", "--", "/bin/true")]
    [InlineData("--policy", "/p.json", "/bin/true")]
    [InlineData("--policy", "/p.json", "--no-landlock", "--", "/bin/true")]
    public void Unknown_or_malformed_options_are_usage_errors(params string[] args)
    {
        Assert.NotNull(Parse(args));
    }

    [Theory]
    [InlineData("{\"version\":1,\"writePaths\":[\"/w/../etc\"]}")]
    [InlineData("{\"version\":1,\"writePaths\":[\"/w\"],\"readPaths\":[\"/usr/../etc\"]}")]
    [InlineData("{\"version\":1,\"writePaths\":[\"/w\"],\"readPaths\":[\"/usr/./lib\"]}")]
    [InlineData("{\"version\":1,\"writePaths\":[\"/w\"],\"readPaths\":[\"/\"]}")]
    [InlineData("{\"version\":1,\"writePaths\":[\"/w\"],\"readPaths\":[\"//\"]}")]
    [InlineData("{\"version\":1,\"writePaths\":[\"/w\\u0000x\"]}")]
    [InlineData("{\"version\":1,\"writePaths\":[\"\"]}")]
    [InlineData("{\"version\":1,\"writePaths\":[\"/w\"],\"readPaths\":[\"\"]}")]
    [InlineData("{\"version\":1,\"writePaths\":[\"/w\"],\"extraSyscalls\":[\"clone3\"]}")]
    [InlineData("{\"version\":1,\"writePaths\":[\"/w\"],\"extraSyscalls\":[\"ioctl\"]}")]
    [InlineData("{\"version\":1,\"writePaths\":[\"/w\"],\"extraSyscalls\":[\"MEMFD_CREATE\"]}")]
    [InlineData("{\"version\":1,\"writePaths\":[\"/w\"],\"addressSpaceBytes\":-1}")]
    [InlineData("{\"version\":1,\"version\":1,\"writePaths\":[\"/w\"],\"readPaths\":[\"/usr\"],\"readPaths\":[\"/\"]}")]
    public void Hostile_policies_are_rejected(string json)
    {
        var policy = HelperPolicyParser.Parse(json, out var error);

        // A duplicate key is accepted by System.Text.Json (last wins); the last value here is "/", which must still be refused.
        Assert.Null(policy);
        Assert.NotNull(error);
    }

    [Fact]
    public void A_policy_cannot_add_any_always_denied_syscall()
    {
        foreach (var name in SeccompProgram.Denied)
        {
            var json = "{\"version\":1,\"writePaths\":[\"/w\"],\"extraSyscalls\":[\"" + name + "\"]}";
            Assert.Null(HelperPolicyParser.Parse(json, out var error));
            Assert.NotNull(error);
        }
    }

    [Fact]
    public void The_target_gets_an_environment_that_is_built_from_the_policy_path_only()
    {
        var source = File.ReadAllText(FindSource("Program.cs"));

        Assert.Contains("new List<string> { \"PATH=\" + policy.Path }", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Environment.GetEnvironmentVariable", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Descriptors_above_stderr_are_marked_close_on_exec_and_a_failure_stops_the_launch()
    {
        var hardening = File.ReadAllText(FindSource("Hardening.cs"));
        var program = File.ReadAllText(FindSource("Program.cs"));

        Assert.Contains("CloseRangeCloexec", hardening, StringComparison.Ordinal);
        Assert.Contains("MarkDescriptorsCloseOnExec() =>", hardening, StringComparison.Ordinal);
        Assert.Contains("failure = Hardening.MarkDescriptorsCloseOnExec();", program, StringComparison.Ordinal);
    }

    [Fact]
    public void Core_dumps_and_address_space_are_limited_before_exec()
    {
        var program = File.ReadAllText(FindSource("Program.cs"));

        Assert.Contains("Native.SetRlimit(Native.RlimitCore, 0, 0)", program, StringComparison.Ordinal);
        Assert.Contains("Native.SetRlimit(Native.RlimitAs,", program, StringComparison.Ordinal);
    }

    private static string FindSource(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Bazaar.Server.DevSandbox.Linux.Helper")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir.FullName, "src", "Bazaar.Server.DevSandbox.Linux.Helper", file);
    }
}
