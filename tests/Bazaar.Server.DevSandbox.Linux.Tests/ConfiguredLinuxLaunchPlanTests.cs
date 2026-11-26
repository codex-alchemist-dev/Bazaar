namespace Bazaar.Server.DevSandbox.Linux.Tests;

public sealed class ConfiguredLinuxLaunchPlanTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("bazaar-plan-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static SandboxRunSpec Spec(string id, string version) =>
        new("run-1", id, version, SandboxEngineKind.Vm, [], [], 1UL);

    private ConfiguredLinuxLaunchPlan Plan() =>
        new(new ConfiguredLinuxLaunchOptions { ModuleRoot = _root, Executable = "/usr/bin/runner", Arguments = ["--dir", "{module}", "--work", "{work}"] });

    [Fact]
    public void Builds_the_command_from_the_installed_module_directory()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, "acme.mod", "1.2.3")).FullName;

        var cmd = Plan().Create(Spec("acme.mod", "1.2.3"), "/tmp/w");

        Assert.Equal("/usr/bin/runner", cmd.Executable);
        Assert.Equal(["--dir", dir, "--work", "/tmp/w"], cmd.Arguments);
        Assert.Contains(dir, cmd.AdditionalReadPaths!);
    }

    [Theory]
    [InlineData("..", "1.0.0")]
    [InlineData("a/b", "1.0.0")]
    [InlineData("acme", "../x")]
    [InlineData("", "1.0.0")]
    public void Refuses_ids_that_could_leave_the_module_root(string id, string version) =>
        Assert.Throws<InvalidOperationException>(() => Plan().Create(Spec(id, version), "/tmp/w"));

    [Fact]
    public void Refuses_a_module_that_is_not_installed() =>
        Assert.Throws<InvalidOperationException>(() => Plan().Create(Spec("missing", "1.0.0"), "/tmp/w"));

    [Fact]
    public void Refuses_relative_options() =>
        Assert.Throws<ArgumentException>(() => new ConfiguredLinuxLaunchPlan(new ConfiguredLinuxLaunchOptions { ModuleRoot = "rel", Executable = "/x" }));
}
