using Bazaar.Client.DefaultApp.Linux;

namespace Bazaar.Client.Linux.Tests;

public class DefaultTerminalCommandLineTests
{
    private const string _binary = "/opt/bazaar/Bazaar.Client.Linux";

    private sealed class FakeRegistrar(DefaultAppStatus status) : IDefaultTerminalRegistrar
    {
        public List<string> Calls { get; } = [];

        public Task<DefaultAppStatus> RegisterAsync(CancellationToken ct)
        {
            Calls.Add("register");
            return Task.FromResult(status);
        }

        public Task<DefaultAppStatus> QueryAsync(CancellationToken ct)
        {
            Calls.Add("query");
            return Task.FromResult(status);
        }

        public Task<DefaultAppStatus> UnregisterAsync(CancellationToken ct)
        {
            Calls.Add("unregister");
            return Task.FromResult(status);
        }
    }

    private static (int? Code, string Out, string Err, FakeRegistrar Registrar) Run(string[] args, string? path, DefaultAppStatus status)
    {
        var registrar = new FakeRegistrar(status);
        var output = new StringWriter();
        var error = new StringWriter();
        var code = DefaultTerminalCommandLine.TryRun(args, path, output, error, _ => registrar, TestContext.Current.CancellationToken);
        return (code, output.ToString(), error.ToString(), registrar);
    }

    [Theory]
    [InlineData("--register-default-terminal", DefaultAppAction.Register)]
    [InlineData("--default-terminal-status", DefaultAppAction.Status)]
    [InlineData("--unregister-default-terminal", DefaultAppAction.Unregister)]
    public void Flags_parse_to_their_action(string flag, DefaultAppAction expected) =>
        Assert.Equal(expected, DefaultTerminalCommandLine.Parse(["--x", flag]));

    [Fact]
    public void Default_app_flags_are_not_terminal_flags() =>
        Assert.Null(DefaultTerminalCommandLine.Parse(["--register-default-app"]));

    [Fact]
    public void Without_a_flag_the_head_starts_normally()
    {
        var run = Run(["--other"], _binary, new DefaultAppStatus(DefaultAppState.Registered, "x", null));
        Assert.Null(run.Code);
        Assert.Empty(run.Registrar.Calls);
    }

    [Fact]
    public void Register_prints_the_status_and_exits_zero_only_when_registered()
    {
        var ok = Run(["--register-default-terminal"], _binary, new DefaultAppStatus(DefaultAppState.Registered, "first", null));
        Assert.Equal(0, ok.Code);
        Assert.Contains("Registered: first", ok.Out, StringComparison.Ordinal);

        var no = Run(["--register-default-terminal"], _binary, new DefaultAppStatus(DefaultAppState.NotDefault, "other", "do this"));
        Assert.Equal(1, no.Code);
        Assert.Contains("Not the default: other", no.Out, StringComparison.Ordinal);
        Assert.Contains("do this", no.Out, StringComparison.Ordinal);
    }

    [Fact]
    public void Unregister_succeeds_when_not_default()
    {
        var run = Run(["--unregister-default-terminal"], _binary, new DefaultAppStatus(DefaultAppState.NotDefault, "gone", null));
        Assert.Equal(0, run.Code);
        Assert.Equal(["unregister"], run.Registrar.Calls);
    }

    [Theory]
    [InlineData("relative")]
    [InlineData("/usr/bin/dotnet")]
    [InlineData(null)]
    public void A_bad_process_path_is_refused_without_running(string? path)
    {
        var run = Run(["--default-terminal-status"], path, new DefaultAppStatus(DefaultAppState.Registered, "x", null));
        Assert.Equal(1, run.Code);
        Assert.Empty(run.Registrar.Calls);
        Assert.Contains("Cannot register", run.Err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Palette_commands_run_through_the_registrar()
    {
        var registrar = new FakeRegistrar(new DefaultAppStatus(DefaultAppState.NotDefault, "n", null));
        string? text = await DefaultTerminalCommands.RunAsync(DefaultTerminalCommands.StatusId, registrar, TestContext.Current.CancellationToken);
        Assert.Contains("Not the default: n", text, StringComparison.Ordinal);
        Assert.Null(await DefaultTerminalCommands.RunAsync("other", registrar, TestContext.Current.CancellationToken));
        Assert.Equal(3, DefaultTerminalCommands.All.Count);
    }
}
