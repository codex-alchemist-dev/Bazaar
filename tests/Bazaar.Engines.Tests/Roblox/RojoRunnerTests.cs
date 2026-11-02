using System.ComponentModel;
using Bazaar.Engines.Roblox.Rojo;

namespace Bazaar.Engines.Tests.Roblox;

public sealed class RojoRunnerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("bazaar-rojo-").FullName;

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
    }

    private string CreateProject(string relativePath)
    {
        string full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "{ \"name\": \"Fixture\", \"tree\": { \"$className\": \"DataModel\" } }");
        return full;
    }

    [Fact]
    public void BuildArguments_is_a_plain_argument_list()
    {
        IReadOnlyList<string> args = RojoRunner.BuildArguments("/root/default.project.json", "/root/out.rbxlx");

        Assert.Equal(["build", "/root/default.project.json", "--output", "/root/out.rbxlx"], args);
    }

    [Fact]
    public async Task BuildAsync_runs_configured_binary_with_build_arguments_in_root()
    {
        string project = CreateProject("game/default.project.json");
        string output = Path.Combine(_root, "game", "out.rbxlx");
        var fake = new FakeProcessRunner();
        var runner = new RojoRunner(new RojoOptions { BinaryPath = "/opt/rojo/rojo", Timeout = TimeSpan.FromSeconds(30) }, fake);

        string result = await runner.BuildAsync(_root, Path.Combine("game", "default.project.json"), Path.Combine("game", "out.rbxlx"), TestContext.Current.CancellationToken);

        FakeProcessRunner.Call call = Assert.Single(fake.Calls);
        Assert.Equal("/opt/rojo/rojo", call.FileName);
        Assert.Equal(["build", project, "--output", output], call.Arguments);
        Assert.Equal(Path.GetFullPath(_root).TrimEnd('/'), call.WorkingDirectory.TrimEnd('/'));
        Assert.Equal(TimeSpan.FromSeconds(30), call.Timeout);
        Assert.Equal(output, result);
    }

    [Fact]
    public async Task BuildAsync_keeps_a_path_with_spaces_as_one_argument()
    {
        string project = CreateProject("my game/default.project.json");
        var fake = new FakeProcessRunner();
        var runner = new RojoRunner(new RojoOptions(), fake);

        await runner.BuildAsync(_root, project, Path.Combine(_root, "my game", "out.rbxl"), TestContext.Current.CancellationToken);

        FakeProcessRunner.Call call = Assert.Single(fake.Calls);
        Assert.Equal(project, call.Arguments[1]);
        Assert.Equal(4, call.Arguments.Count);
    }

    [Fact]
    public async Task BuildAsync_non_zero_exit_is_NonZeroExit_with_stderr_captured()
    {
        string project = CreateProject("default.project.json");
        var fake = new FakeProcessRunner
        {
            Respond = _ => new ProcessResult(2, string.Empty, "error: bad tree\nmore detail\n", TimedOut: false),
        };
        var runner = new RojoRunner(new RojoOptions(), fake);

        RojoException ex = await Assert.ThrowsAsync<RojoException>(() =>
            runner.BuildAsync(_root, project, Path.Combine(_root, "out.rbxl"), TestContext.Current.CancellationToken));

        Assert.Equal(RojoErrorKind.NonZeroExit, ex.Kind);
        Assert.Equal(2, ex.ExitCode);
        Assert.Equal("error: bad tree\nmore detail\n", ex.StandardError);
        Assert.Contains("error: bad tree", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildAsync_timeout_is_Timeout()
    {
        string project = CreateProject("default.project.json");
        var fake = new FakeProcessRunner
        {
            Respond = _ => new ProcessResult(-1, string.Empty, "partial", TimedOut: true),
        };
        var runner = new RojoRunner(new RojoOptions(), fake);

        RojoException ex = await Assert.ThrowsAsync<RojoException>(() =>
            runner.BuildAsync(_root, project, Path.Combine(_root, "out.rbxl"), TestContext.Current.CancellationToken));

        Assert.Equal(RojoErrorKind.Timeout, ex.Kind);
        Assert.Equal("partial", ex.StandardError);
    }

    [Fact]
    public async Task Missing_binary_is_BinaryNotFound()
    {
        string project = CreateProject("default.project.json");
        var fake = new FakeProcessRunner { ThrowOnStart = new Win32Exception(2, "No such file or directory") };
        var runner = new RojoRunner(new RojoOptions { BinaryPath = "nope-rojo" }, fake);

        RojoException ex = await Assert.ThrowsAsync<RojoException>(() =>
            runner.BuildAsync(_root, project, Path.Combine(_root, "out.rbxl"), TestContext.Current.CancellationToken));

        Assert.Equal(RojoErrorKind.BinaryNotFound, ex.Kind);
        Assert.Contains("nope-rojo", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Caller_cancellation_propagates_as_OperationCanceledException()
    {
        string project = CreateProject("default.project.json");
        var fake = new FakeProcessRunner();
        var runner = new RojoRunner(new RojoOptions(), fake);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.BuildAsync(_root, project, Path.Combine(_root, "out.rbxl"), cts.Token));
    }

    [Fact]
    public async Task Build_with_missing_project_fails_before_running_rojo()
    {
        var fake = new FakeProcessRunner();
        var runner = new RojoRunner(new RojoOptions(), fake);

        RojoException ex = await Assert.ThrowsAsync<RojoException>(() =>
            runner.BuildAsync(_root, "absent.project.json", Path.Combine(_root, "out.rbxl"), TestContext.Current.CancellationToken));

        Assert.Equal(RojoErrorKind.ProjectNotFound, ex.Kind);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task Build_with_output_directory_missing_fails_before_running_rojo()
    {
        string project = CreateProject("default.project.json");
        var fake = new FakeProcessRunner();
        var runner = new RojoRunner(new RojoOptions(), fake);

        RojoException ex = await Assert.ThrowsAsync<RojoException>(() =>
            runner.BuildAsync(_root, project, Path.Combine(_root, "no-such-dir", "out.rbxl"), TestContext.Current.CancellationToken));

        Assert.Equal(RojoErrorKind.OutputDirectoryNotFound, ex.Kind);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task Build_with_project_outside_root_fails_before_running_rojo()
    {
        string outside = CreateProject("../escape/default.project.json");
        var fake = new FakeProcessRunner();
        var runner = new RojoRunner(new RojoOptions(), fake);

        RojoException ex = await Assert.ThrowsAsync<RojoException>(() =>
            runner.BuildAsync(_root, Path.Combine("..", "escape", "default.project.json"), Path.Combine(_root, "out.rbxl"), TestContext.Current.CancellationToken));

        Assert.Equal(RojoErrorKind.PathOutsideRoot, ex.Kind);
        Assert.Empty(fake.Calls);
        _ = outside;
    }

    [Fact]
    public async Task GetVersionAsync_runs_version_flag_and_returns_trimmed_output()
    {
        var fake = new FakeProcessRunner { Respond = _ => new ProcessResult(0, "  Rojo 7.4.1\n", string.Empty, TimedOut: false) };
        var runner = new RojoRunner(new RojoOptions { BinaryPath = "rojo" }, fake);

        string version = await runner.GetVersionAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Rojo 7.4.1", version);
        Assert.Equal(["--version"], Assert.Single(fake.Calls).Arguments);
    }

    [Fact]
    public async Task GetVersionAsync_with_no_pin_accepts_any_version()
    {
        var fake = new FakeProcessRunner { Respond = _ => new ProcessResult(0, "anything 0.0.1", string.Empty, TimedOut: false) };
        var runner = new RojoRunner(new RojoOptions(), fake);

        Assert.Equal("anything 0.0.1", await runner.GetVersionAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("Rojo 7.4.1", "7.4.1")]
    [InlineData("Rojo v7.4.1", "v7.4.1")]
    [InlineData("rojo 7.4.1", "7.4.1")]
    public async Task GetVersionAsync_matching_pin_passes(string output, string pin)
    {
        var fake = new FakeProcessRunner { Respond = _ => new ProcessResult(0, output, string.Empty, TimedOut: false) };
        var runner = new RojoRunner(new RojoOptions { RequiredVersion = pin }, fake);

        Assert.Equal(output, await runner.GetVersionAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetVersionAsync_pin_is_token_equal_not_substring()
    {
        var fake = new FakeProcessRunner { Respond = _ => new ProcessResult(0, "Rojo 7.4.10", string.Empty, TimedOut: false) };
        var runner = new RojoRunner(new RojoOptions { RequiredVersion = "7.4.1" }, fake);

        RojoException ex = await Assert.ThrowsAsync<RojoException>(() => runner.GetVersionAsync(TestContext.Current.CancellationToken));

        Assert.Equal(RojoErrorKind.VersionMismatch, ex.Kind);
    }

    [Fact]
    public async Task GetVersionAsync_empty_output_is_EmptyVersionOutput()
    {
        var fake = new FakeProcessRunner { Respond = _ => new ProcessResult(0, "   \n", string.Empty, TimedOut: false) };
        var runner = new RojoRunner(new RojoOptions(), fake);

        RojoException ex = await Assert.ThrowsAsync<RojoException>(() => runner.GetVersionAsync(TestContext.Current.CancellationToken));

        Assert.Equal(RojoErrorKind.EmptyVersionOutput, ex.Kind);
    }

    [Fact]
    public async Task GetVersionAsync_non_zero_exit_is_NonZeroExit()
    {
        var fake = new FakeProcessRunner { Respond = _ => new ProcessResult(1, string.Empty, "unknown flag", TimedOut: false) };
        var runner = new RojoRunner(new RojoOptions(), fake);

        RojoException ex = await Assert.ThrowsAsync<RojoException>(() => runner.GetVersionAsync(TestContext.Current.CancellationToken));

        Assert.Equal(RojoErrorKind.NonZeroExit, ex.Kind);
        Assert.Equal("unknown flag", ex.StandardError);
    }

    [Fact]
    public void Empty_binary_path_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => new RojoRunner(new RojoOptions { BinaryPath = "  " }));
    }

    [Fact]
    public void Non_positive_timeout_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => new RojoRunner(new RojoOptions { Timeout = TimeSpan.Zero }));
    }
}
