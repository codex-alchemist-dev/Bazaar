using Bazaar.Admin;
using Bazaar.Client.Tui.Dashboard;
using Bazaar.Client.Tui.Installer;
using Bazaar.Server.Setup;
using Bazaar.Server.Setup.Host;

namespace Bazaar.Client.Tui.Host;

/// <summary>Everything the command line touches outside itself, so a test runs it whole with a scripted terminal and fake wiring.</summary>
public sealed record TuiHostEnvironment(
    ITerminal Terminal,
    TextReader Input,
    TextWriter Output,
    TextWriter Error,
    TerminalEnvironment TerminalEnvironment,
    IReadOnlyDictionary<string, string?> Variables,
    ITuiHostWiring Wiring,
    ISecretValueSource? Secrets = null,
    TimeProvider? Clock = null,
    Func<TimeSpan, CancellationToken, Task>? Delay = null);

/// <summary>
/// The program (doc 58 work packages 4 and 5): the installer mode and the dashboard mode behind one entry. It picks the mode, builds
/// the pieces through <see cref="ITuiHostWiring"/>, and hands them to the heads that already exist (<see cref="TuiInstaller"/> and
/// <see cref="DashboardShell"/>). It adds no setup logic and no screen logic.
/// </summary>
public static class TuiHostCommandLine
{
    public const int Ok = 0;
    public const int SignInRefused = 1;
    public const int UsageOrUnreachable = 2;
    public const int Crashed = 3;

    private const int _crashLookback = 20;

    public static async Task<int> RunAsync(IReadOnlyList<string> args, TuiHostEnvironment host, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(host);

        var parsed = TuiHostArguments.Parse(args);
        if (parsed.Command == TuiHostCommand.Help)
        {
            host.Output.WriteLine(TuiHostArguments.Usage);
            return Ok;
        }

        if (parsed.Error is not null)
        {
            host.Error.WriteLine(parsed.Error);
            host.Error.WriteLine(TuiHostArguments.Usage);
            return UsageOrUnreachable;
        }

        var command = parsed.Command;
        if (command == TuiHostCommand.Auto)
        {
            command = await ChooseAsync(host, ct).ConfigureAwait(false);
        }

        return command == TuiHostCommand.Install
            ? await InstallAsync(host, ct).ConfigureAwait(false)
            : await OpsAsync(host, ct).ConfigureAwait(false);
    }

    /// <summary>Doc 58 section 2: install mode when no owner exists, ops mode after. No reachable database means no owner yet.</summary>
    private static async Task<TuiHostCommand> ChooseAsync(TuiHostEnvironment host, CancellationToken ct)
    {
        await using var ops = await TryOpenOpsAsync(host, ct).ConfigureAwait(false);
        if (ops is not null && await ops.OwnerExistsAsync(ct).ConfigureAwait(false))
        {
            return TuiHostCommand.Ops;
        }

        return TuiHostCommand.Install;
    }

    private static async Task<int> InstallAsync(TuiHostEnvironment host, CancellationToken ct)
    {
        await using var wiring = await host.Wiring.OpenInstallAsync(host.Variables, ct).ConfigureAwait(false);
        var options = new TuiInstallerOptions(
            host.TerminalEnvironment,
            host.Terminal,
            TuiInstaller.SharedBackend(host.Variables, wiring.Gate, RegistrySetupSettingCatalog.Known),
            () => new ConsoleSetupPrompter(host.Input, host.Output, host.Secrets),
            SecretWriter: new UnavailableSecretWriter(),
            FullScreenHostFactory: TuiInstaller.TerminalGuiHostFactory());
        var run = await TuiInstaller.RunAsync(options, ct).ConfigureAwait(false);
        return run.Result.ExitCode;
    }

    private static async Task<int> OpsAsync(TuiHostEnvironment host, CancellationToken ct)
    {
        await using var wiring = await TryOpenOpsAsync(host, ct).ConfigureAwait(false);
        if (wiring is null)
        {
            host.Error.WriteLine("The server database cannot be reached. Set the database connection reference the way bazaar-admin reads it, then start again.");
            return UsageOrUnreachable;
        }

        var clock = host.Clock ?? TimeProvider.System;
        var mode = TuiModeSelector.Select(host.TerminalEnvironment);
        var style = mode.Mode == TuiMode.FullScreen ? new TuiStyle(useColor: true) : TuiStyle.Plain;
        var shell = new DashboardShell(host.Terminal, wiring.Ports, wiring.SignIn, style, clock);
        try
        {
            return mode.Mode == TuiMode.FullScreen
                ? await shell.RunAsync(ct).ConfigureAwait(false)
                : await SnapshotAsync(shell, wiring, host, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Ok;
        }
        catch (Exception exception)
        {
            return await CrashAsync(exception, wiring, host, clock, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The plain path: sign in as the owner on the box, print one frame, sign out. There is no code door here: a code is typed, and the
    /// plain path reads no secret.
    /// </summary>
    private static async Task<int> SnapshotAsync(DashboardShell shell, OpsWiring wiring, TuiHostEnvironment host, CancellationToken ct)
    {
        var session = await wiring.SignIn.SignInOwnerAsync(ct).ConfigureAwait(false);
        if (session is null)
        {
            host.Error.WriteLine("Sign-in refused: the owner's local gate is closed on this machine.");
            return SignInRefused;
        }

        try
        {
            var frame = await shell.RenderAsync(ct).ConfigureAwait(false);
            foreach (var line in frame.Lines)
            {
                host.Terminal.WriteLine(line);
            }

            return Ok;
        }
        finally
        {
            await wiring.SignIn.SignOutAsync(session, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A crash is recorded first, so the next start counts it (doc 58 section 3a), then the program waits the bounded restart delay
    /// and exits non-zero for the service manager to relaunch. Only the exception's type is recorded: its message can carry text from
    /// a port.
    /// </summary>
    private static async Task<int> CrashAsync(Exception exception, OpsWiring wiring, TuiHostEnvironment host, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        try
        {
            await wiring.Crashes.RecordAsync(new OpsCrashNote(now, "crashed with " + exception.GetType().Name), CancellationToken.None).ConfigureAwait(false);
            var times = (await wiring.Crashes.ReadRecentAsync(_crashLookback, CancellationToken.None).ConfigureAwait(false)).Select(c => c.At).ToList();
            var delay = new RestartDelayPolicy().DelayAfter(times, now);
            host.Error.WriteLine($"The dashboard crashed ({exception.GetType().Name}). Waiting {delay.TotalMinutes:0} minute(s) before it exits, so a restart is no faster.");
            await (host.Delay ?? Task.Delay)(delay, ct).ConfigureAwait(false);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // The journal itself failed: the exit code still tells the service manager.
            host.Error.WriteLine($"The dashboard crashed ({exception.GetType().Name}) and the crash could not be recorded.");
        }

        return Crashed;
    }

    private static async Task<OpsWiring?> TryOpenOpsAsync(TuiHostEnvironment host, CancellationToken ct)
    {
        try
        {
            return await host.Wiring.OpenOpsAsync(host.Variables, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The type only: a connection error can carry the connection string.
            host.Error.WriteLine("The server database could not be opened (" + exception.GetType().Name + ").");
            return null;
        }
    }
}
