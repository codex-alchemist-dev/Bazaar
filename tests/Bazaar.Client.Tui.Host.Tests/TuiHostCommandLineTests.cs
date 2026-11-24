using Bazaar.Client.Tui.Dashboard;
using Bazaar.Client.Tui.Tests;
using Bazaar.Server.OpsPorts;
using Bazaar.Server.WebAdmin;

namespace Bazaar.Client.Tui.Host.Tests;

public sealed class TuiHostCommandLineTests : IDisposable
{
    private static readonly IReadOnlyDictionary<string, string?> _noVariables = new Dictionary<string, string?>(StringComparer.Ordinal);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bazaar-tuihost-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    // The answers of the plain wizard's default-heavy path (the same list bazaar-admin's wizard tests use); the last "" declines to apply.
    private static readonly string[] _wizardAnswers =
    [
        "Example Studio", "", "Owner", "", "dpapi", "dpapi:C:\\ProgramData\\Bazaar\\kek.bin", "", "",
        "https://anchor.example.invalid/bazaar-audit", "file:/mnt/backup/bazaar", "", "", "", "", "", "", "", "",
    ];

    private string CrashFile => Path.Combine(_dir, "crash.jsonl");

    private static TerminalEnvironment KeyTerminal(params string[] arguments) => new(false, false, null, "xterm", arguments);

    private static TuiHostEnvironment Host(
        ITerminal terminal,
        FakeWiring wiring,
        TerminalEnvironment environment,
        StringWriter output,
        StringWriter error,
        string input = "",
        Func<TimeSpan, CancellationToken, Task>? delay = null) =>
        new(terminal, new StringReader(input), output, error, environment, _noVariables, wiring, Secrets: null, Clock: new FixedClock(FakeWiring.Now), Delay: delay);

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("help")]
    public async Task Help_prints_the_usage_and_exits_zero_without_opening_anything(string argument)
    {
        var wiring = new FakeWiring();
        var output = new StringWriter();

        var code = await TuiHostCommandLine.RunAsync([argument], Host(new ScriptedTerminal(), wiring, KeyTerminal(), output, new StringWriter()));

        Assert.Equal(TuiHostCommandLine.Ok, code);
        Assert.Contains("bazaar-tui [install | ops]", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, wiring.OpsOpened + wiring.InstallOpened);
    }

    [Theory]
    [InlineData("frobnicate")]
    [InlineData("--nope")]
    public async Task An_unknown_argument_exits_two_with_the_usage_on_stderr(string argument)
    {
        var error = new StringWriter();

        var code = await TuiHostCommandLine.RunAsync([argument], Host(new ScriptedTerminal(), new FakeWiring(), KeyTerminal(), new StringWriter(), error));

        Assert.Equal(TuiHostCommandLine.UsageOrUnreachable, code);
        Assert.Contains($"Unknown argument '{argument}'", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("bazaar-tui [install | ops]", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Naming_both_modes_is_a_usage_error()
    {
        var error = new StringWriter();

        var code = await TuiHostCommandLine.RunAsync(["install", "ops"], Host(new ScriptedTerminal(), new FakeWiring(), KeyTerminal(), new StringWriter(), error));

        Assert.Equal(TuiHostCommandLine.UsageOrUnreachable, code);
        Assert.Contains("not both", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_mode_flags_are_accepted_arguments()
    {
        var wiring = new FakeWiring();

        var code = await TuiHostCommandLine.RunAsync(["ops", "--plain", "--screen-reader"], Host(new ScriptedTerminal(), wiring, KeyTerminal("--plain"), new StringWriter(), new StringWriter()));

        // No database: ops reports it cannot reach it, which proves the arguments were not rejected.
        Assert.Equal(TuiHostCommandLine.UsageOrUnreachable, code);
        Assert.Equal(1, wiring.OpsOpened);
    }

    [Fact]
    public async Task Ops_without_a_reachable_database_says_so_and_exits_two()
    {
        var error = new StringWriter();

        var code = await TuiHostCommandLine.RunAsync(["ops"], Host(new ScriptedTerminal(), new FakeWiring { Ops = null }, KeyTerminal(), new StringWriter(), error));

        Assert.Equal(TuiHostCommandLine.UsageOrUnreachable, code);
        Assert.Contains("database cannot be reached", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_no_mode_named_it_installs_when_there_is_no_database_and_when_there_is_no_owner()
    {
        var noDatabase = new FakeWiring { Ops = null };
        var (opsNoOwner, _, _, _) = FakeWiring.OpsOver(FakeWiring.Owner, CrashFile, ownerExists: false);
        var noOwner = new FakeWiring { Ops = opsNoOwner };

        await TuiHostCommandLine.RunAsync([], Host(new ScriptedTerminal(), noDatabase, KeyTerminal("--plain"), new StringWriter(), new StringWriter()));
        await TuiHostCommandLine.RunAsync([], Host(new ScriptedTerminal(), noOwner, KeyTerminal("--plain"), new StringWriter(), new StringWriter()));

        Assert.Equal(1, noDatabase.InstallOpened);
        Assert.Equal(1, noOwner.InstallOpened);
    }

    [Fact]
    public async Task With_no_mode_named_it_opens_the_dashboard_once_an_owner_exists()
    {
        var (ops, audit, _, _) = FakeWiring.OpsOver(FakeWiring.Owner, CrashFile, ownerExists: true, new FakeStatus());
        var wiring = new FakeWiring { Ops = ops };
        var terminal = new ScriptedTerminal();

        var code = await TuiHostCommandLine.RunAsync(["--plain"], Host(terminal, wiring, KeyTerminal("--plain"), new StringWriter(), new StringWriter()));

        Assert.Equal(TuiHostCommandLine.Ok, code);
        Assert.Equal(0, wiring.InstallOpened);
        Assert.Contains("Test Studio", terminal.Output, StringComparison.Ordinal);
        Assert.Equal([AdminAuditKinds.SignIn, AdminAuditKinds.SignOut], audit.Entries.Select(e => e.Kind));
    }

    [Fact]
    public async Task The_plain_dashboard_signs_in_the_owner_prints_one_frame_without_escape_codes_and_signs_out()
    {
        var (ops, audit, _, sessions) = FakeWiring.OpsOver(FakeWiring.Owner, CrashFile, ownerExists: true, new FakeStatus());
        var terminal = new ScriptedTerminal();

        var code = await TuiHostCommandLine.RunAsync(["ops", "--plain"], Host(terminal, new FakeWiring { Ops = ops }, KeyTerminal("--plain"), new StringWriter(), new StringWriter()));

        Assert.Equal(TuiHostCommandLine.Ok, code);
        Assert.DoesNotContain('\u001b', terminal.Output);
        Assert.Contains("Test Studio", terminal.Output, StringComparison.Ordinal);
        Assert.Equal(0, terminal.ErasedLines);
        Assert.Equal([AdminAuditKinds.SignIn, AdminAuditKinds.SignOut], audit.Entries.Select(e => e.Kind));
        Assert.Null(await sessions.ValidateAsync("anything", FakeWiring.Now));
    }

    [Theory]
    [InlineData(true, false, null, "xterm")]
    [InlineData(false, true, null, "xterm")]
    [InlineData(false, false, "1", "xterm")]
    [InlineData(false, false, null, "dumb")]
    public async Task Output_that_is_not_a_terminal_or_a_plain_signal_takes_the_plain_dashboard_path(bool inputRedirected, bool outputRedirected, string? noColor, string term)
    {
        var (ops, _, _, _) = FakeWiring.OpsOver(FakeWiring.Owner, CrashFile, ownerExists: true, new FakeStatus());
        var terminal = new ScriptedTerminal();
        var environment = new TerminalEnvironment(inputRedirected, outputRedirected, noColor, term, []);

        var code = await TuiHostCommandLine.RunAsync(["ops"], Host(terminal, new FakeWiring { Ops = ops }, environment, new StringWriter(), new StringWriter()));

        Assert.Equal(TuiHostCommandLine.Ok, code);
        Assert.DoesNotContain('\u001b', terminal.Output);
        Assert.Contains("Test Studio", terminal.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_closed_owner_gate_exits_one_in_the_plain_path_and_audits_the_denial()
    {
        var (ops, audit, _, _) = FakeWiring.OpsOver(owner: null, CrashFile, ownerExists: true);
        var error = new StringWriter();

        var code = await TuiHostCommandLine.RunAsync(["ops", "--plain"], Host(new ScriptedTerminal(), new FakeWiring { Ops = ops }, KeyTerminal("--plain"), new StringWriter(), error));

        Assert.Equal(TuiHostCommandLine.SignInRefused, code);
        Assert.Contains("Sign-in refused", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(AdminAuditKinds.Denied, Assert.Single(audit.Entries).Kind);
    }

    [Fact]
    public async Task The_full_screen_dashboard_signs_in_the_owner_and_leaves_with_ctrl_c()
    {
        var (ops, audit, _, _) = FakeWiring.OpsOver(FakeWiring.Owner, CrashFile, ownerExists: true, new FakeStatus());
        var terminal = new ScriptedTerminal(supportsKeys: true).Keys(
            new TerminalKey(TerminalKeyKind.Enter), // "Owner on this box" is the default pick
            new TerminalKey(TerminalKeyKind.Cancel));

        var code = await TuiHostCommandLine.RunAsync(["ops"], Host(terminal, new FakeWiring { Ops = ops }, KeyTerminal(), new StringWriter(), new StringWriter()));

        Assert.Equal(TuiHostCommandLine.Ok, code);
        Assert.Contains("Test Studio", terminal.Output, StringComparison.Ordinal);
        Assert.Equal([AdminAuditKinds.SignIn, AdminAuditKinds.SignOut], audit.Entries.Select(e => e.Kind));
    }

    [Fact]
    public async Task A_dashboard_crash_is_journalled_by_type_only_waits_the_bounded_delay_and_exits_three()
    {
        var (ops, _, crashes, _) = FakeWiring.OpsOver(FakeWiring.Owner, CrashFile, ownerExists: true, new FakeStatus());
        var delays = new List<TimeSpan>();
        var error = new StringWriter();
        Task Delay(TimeSpan span, CancellationToken ct)
        {
            delays.Add(span);
            return Task.CompletedTask;
        }

        var code = await TuiHostCommandLine.RunAsync(["ops"], Host(new ExplodingTerminal(), new FakeWiring { Ops = ops }, KeyTerminal(), new StringWriter(), error, delay: Delay));

        Assert.Equal(TuiHostCommandLine.Crashed, code);
        var note = Assert.Single(await crashes.ReadRecentAsync(10));
        Assert.Equal("crashed with InvalidOperationException", note.Reason);
        Assert.DoesNotContain("secret detail", note.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("secret detail", error.ToString(), StringComparison.Ordinal);
        Assert.Equal([TimeSpan.FromMinutes(1)], delays);
    }

    [Fact]
    public async Task Repeated_crashes_follow_the_one_one_five_minute_delays()
    {
        var delays = new List<TimeSpan>();
        Task Delay(TimeSpan span, CancellationToken ct)
        {
            delays.Add(span);
            return Task.CompletedTask;
        }

        for (var run = 0; run < 4; run++)
        {
            var (ops, _, _, _) = FakeWiring.OpsOver(FakeWiring.Owner, CrashFile, ownerExists: true, new FakeStatus());
            await TuiHostCommandLine.RunAsync(["ops"], Host(new ExplodingTerminal(), new FakeWiring { Ops = ops }, KeyTerminal(), new StringWriter(), new StringWriter(), delay: Delay));
        }

        Assert.Equal([1, 1, 5, 5], delays.Select(d => (int)d.TotalMinutes));
    }

    [Fact]
    public async Task The_installer_runs_the_real_wizard_on_the_plain_path_and_closes_the_database()
    {
        var wiring = new FakeWiring();
        var terminal = new ScriptedTerminal();
        // The same answers the installer tests use for the real wizard: the plain prompter reads them line by line.
        var answers = string.Join('\n', _wizardAnswers) + "\n";
        var output = new StringWriter();

        var code = await TuiHostCommandLine.RunAsync(["install", "--plain"], Host(terminal, wiring, KeyTerminal("--plain"), output, new StringWriter(), answers));

        Assert.Equal(1, wiring.InstallOpened);
        Assert.True(wiring.InstallDisposed);
        Assert.Equal(0, wiring.OpsOpened);
        Assert.True((output.ToString() + terminal.Output).Contains("Plan hash", StringComparison.Ordinal), output.ToString().Replace("\n", " ¶ "));
        Assert.Equal(0, code);
    }

    [Fact]
    public void The_manifest_names_the_program_and_the_modules_it_needs()
    {
        var manifest = TuiHostManifest.Definition;

        Assert.Equal("tui-host", manifest.Id);
        Assert.Contains("feature:server-tui", manifest.Provides);
        Assert.Contains("ops-ports", manifest.DependsOn);
    }

    [Fact]
    public void The_program_has_no_direct_reference_to_bazaar_server_or_client_core()
    {
        var project = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Bazaar.Client.Tui.Host", "Bazaar.Client.Tui.Host.csproj");
        var text = File.ReadAllText(project);

        Assert.DoesNotContain("Bazaar.Server\\Bazaar.Server.csproj", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Bazaar.Client.Core", text, StringComparison.Ordinal);
    }
}
