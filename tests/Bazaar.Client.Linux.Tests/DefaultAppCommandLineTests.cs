using Bazaar.Client.Commands;
using Bazaar.Client.DefaultApp.Linux;

namespace Bazaar.Client.Linux.Tests;

public class DefaultAppCommandLineTests
{
    private const string _binary = "/opt/bazaar/Bazaar.Client.Linux";

    private sealed class FakeRegistrar(DefaultAppStatus status) : IDefaultAppRegistrar
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

    private static (int? Code, string Out, string Err, FakeRegistrar Registrar, DefaultAppSettings? Settings) Run(
        string[] args,
        string? path,
        DefaultAppStatus status)
    {
        var registrar = new FakeRegistrar(status);
        DefaultAppSettings? seen = null;
        var output = new StringWriter();
        var error = new StringWriter();
        var code = DefaultAppCommandLine.TryRun(args, path, output, error, s =>
        {
            seen = s;
            return registrar;
        }, TestContext.Current.CancellationToken);
        return (code, output.ToString(), error.ToString(), registrar, seen);
    }

    [Theory]
    [InlineData("--register-default-app", DefaultAppAction.Register)]
    [InlineData("--default-app-status", DefaultAppAction.Status)]
    [InlineData("--unregister-default-app", DefaultAppAction.Unregister)]
    public void Flags_parse_to_their_action(string flag, DefaultAppAction expected)
    {
        Assert.Equal(expected, DefaultAppCommandLine.Parse(["--preload", flag]));
    }

    [Fact]
    public void Without_a_flag_nothing_runs_and_the_head_starts_normally()
    {
        var (code, output, _, registrar, settings) = Run(["--preload"], _binary, new DefaultAppStatus(DefaultAppState.Registered, "x", null));

        Assert.Null(code);
        Assert.Equal(string.Empty, output);
        Assert.Empty(registrar.Calls);
        Assert.Null(settings);
    }

    [Fact]
    public void Register_passes_the_real_binary_path_and_exits_zero_when_registered()
    {
        var (code, output, _, registrar, settings) = Run(
            ["--register-default-app"],
            _binary,
            new DefaultAppStatus(DefaultAppState.Registered, "Bazaar is the default.", null));

        Assert.Equal(0, code);
        Assert.Equal(["register"], registrar.Calls);
        Assert.Equal(_binary, settings!.ExecPath);
        Assert.Equal("Registered: Bazaar is the default." + Environment.NewLine, output);
    }

    [Fact]
    public void Not_default_prints_the_guidance_and_exits_one()
    {
        var (code, output, _, _, _) = Run(
            ["--register-default-app"],
            _binary,
            new DefaultAppStatus(DefaultAppState.NotDefault, "Another app handles it.", "Open Settings, Default applications."));

        Assert.Equal(1, code);
        Assert.StartsWith("Not the default: Another app handles it.", output, StringComparison.Ordinal);
        Assert.Contains("Open Settings, Default applications.", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Registered", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Unavailable_exits_one()
    {
        var (code, output, _, _, _) = Run(
            ["--default-app-status"],
            _binary,
            new DefaultAppStatus(DefaultAppState.Unavailable, "xdg-mime is not available.", null));

        Assert.Equal(1, code);
        Assert.Equal("Unavailable: xdg-mime is not available." + Environment.NewLine, output);
    }

    [Fact]
    public void Status_only_queries()
    {
        var (code, _, _, registrar, _) = Run(
            ["--default-app-status"],
            _binary,
            new DefaultAppStatus(DefaultAppState.Registered, "ok", null));

        Assert.Equal(0, code);
        Assert.Equal(["query"], registrar.Calls);
    }

    [Fact]
    public void Unregister_exits_zero_when_the_entry_is_gone_and_one_when_unavailable()
    {
        var gone = Run(["--unregister-default-app"], _binary, new DefaultAppStatus(DefaultAppState.NotDefault, "Removed.", null));
        var failed = Run(["--unregister-default-app"], _binary, new DefaultAppStatus(DefaultAppState.Unavailable, "Could not remove.", null));

        Assert.Equal(0, gone.Code);
        Assert.Equal(["unregister"], gone.Registrar.Calls);
        Assert.Equal(1, failed.Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Bazaar.Client.Linux")]
    [InlineData("./Bazaar.Client.Linux")]
    [InlineData("/usr/share/dotnet/dotnet")]
    [InlineData("/usr/bin/dotnet")]
    public void A_bad_process_path_is_refused_without_touching_the_registrar(string? path)
    {
        var (code, output, error, registrar, settings) = Run(
            ["--register-default-app"],
            path,
            new DefaultAppStatus(DefaultAppState.Registered, "x", null));

        Assert.Equal(1, code);
        Assert.Empty(registrar.Calls);
        Assert.Null(settings);
        Assert.Equal(string.Empty, output);
        Assert.Contains("Cannot register", error, StringComparison.Ordinal);
        Assert.Contains("full path", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Unsafe_settings_from_the_registrar_are_reported_not_thrown()
    {
        var error = new StringWriter();

        var code = DefaultAppCommandLine.TryRun(
            ["--register-default-app"],
            "/opt/it's/Bazaar",
            new StringWriter(),
            error,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, code);
        Assert.Contains("refused", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_palette_commands_load_into_a_registry_and_are_local()
    {
        var registry = CommandRegistry.Load([.. DefaultAppCommands.All], []).Registry;

        Assert.Equal(3, registry.Commands.Count);
        Assert.All(registry.Commands, c => Assert.Equal(CommandSource.Local, c.Source));
        Assert.Equal(
            [DefaultAppCommands.RegisterId, DefaultAppCommands.StatusId, DefaultAppCommands.UnregisterId],
            registry.Commands.Select(c => c.Id));
    }

    [Theory]
    [InlineData(DefaultAppCommands.RegisterId, "register")]
    [InlineData(DefaultAppCommands.StatusId, "query")]
    [InlineData(DefaultAppCommands.UnregisterId, "unregister")]
    public async Task A_palette_command_runs_the_matching_registrar_call_and_returns_honest_text(string id, string call)
    {
        var registrar = new FakeRegistrar(new DefaultAppStatus(DefaultAppState.NotDefault, "Another app handles it.", "Open Settings."));

        var text = await DefaultAppCommands.RunAsync(id, registrar, TestContext.Current.CancellationToken);

        Assert.Equal([call], registrar.Calls);
        Assert.StartsWith("Not the default: Another app handles it.", text, StringComparison.Ordinal);
        Assert.Contains("Open Settings.", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_command_id_returns_null()
    {
        var registrar = new FakeRegistrar(new DefaultAppStatus(DefaultAppState.Registered, "x", null));

        Assert.Null(await DefaultAppCommands.RunAsync("update.check", registrar, TestContext.Current.CancellationToken));
        Assert.Empty(registrar.Calls);
    }
}
