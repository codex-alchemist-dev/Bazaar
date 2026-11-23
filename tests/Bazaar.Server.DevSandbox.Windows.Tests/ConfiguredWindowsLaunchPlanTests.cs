namespace Bazaar.Server.DevSandbox.Windows.Tests;

public sealed class ConfiguredWindowsLaunchPlanTests
{
    private static SandboxRunSpec Spec(string id, string version) =>
        new("run-1", id, version, SandboxEngineKind.Vm, [], [], 1UL);

    private static ConfiguredWindowsLaunchPlan Plan() =>
        new(new ConfiguredWindowsLaunchOptions { ModuleRoot = "C:\\bazaar\\modules", Executable = "C:\\Program Files\\Runner\\runner.exe" });

    [Theory]
    [InlineData("..", "1.0.0")]
    [InlineData("a\\b", "1.0.0")]
    [InlineData("acme", "..\\x")]
    [InlineData("", "1.0.0")]
    public void Refuses_ids_that_could_leave_the_module_root(string id, string version) =>
        Assert.Throws<InvalidOperationException>(() => Plan().Create(Spec(id, version), "C:\\w"));

    [Fact]
    public void Refuses_a_module_that_is_not_installed() =>
        Assert.Throws<InvalidOperationException>(() => Plan().Create(Spec("missing", "1.0.0"), "C:\\w"));

    [Theory]
    [InlineData("rel", "C:\\x.exe")]
    [InlineData("\\\\server\\share", "C:\\x.exe")]
    [InlineData("C:\\ok", "x.exe")]
    [InlineData("C:\\ok\\..\\up", "C:\\x.exe")]
    public void Refuses_options_that_are_not_drive_letter_paths(string root, string exe) =>
        Assert.Throws<ArgumentException>(() => new ConfiguredWindowsLaunchPlan(new ConfiguredWindowsLaunchOptions { ModuleRoot = root, Executable = exe }));
}
