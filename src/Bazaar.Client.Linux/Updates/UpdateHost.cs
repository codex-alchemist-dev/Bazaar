using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Threading;
using Bazaar.Client.Abstractions.Server;
using Bazaar.Client.Commands;
using Bazaar.Client.Updater.Linux;
using Bazaar.CompanyIdentity;
using Bazaar.Contracts.Releases;
using Bazaar.Contracts.Wire;

namespace Bazaar.Client.Linux.Updates;

/// <summary>
/// Composition for the head (doc 30 section 7): reads the settings, builds the updater on the pinned connection to the company
/// server, and starts the one background check. With updates off or the settings invalid, no updater is built and the
/// coordinator only reports why.
/// </summary>
public sealed class UpdateHost : IDisposable
{
    /// <summary>The protocol this client speaks, as <c>major.minor</c> (matches the wire client's defaults).</summary>
    public const string RunningProtocol = "1.0";

    private static readonly TimeSpan _startDelay = TimeSpan.FromSeconds(30);

    private readonly HttpClient? _http;

    private UpdateHost(UpdateCoordinator coordinator, WelcomeCompatibilityHolder compat, HttpClient? http)
    {
        Coordinator = coordinator;
        Compatibility = compat;
        _http = http;
        // No command palette exists in any head yet (only the CommandRegistry/CommandMatcher library in Bazaar.Client.Commands),
        // so the head owns a registry holding just its local commands. Still missing: a palette view that opens on its mnemonic,
        // lists Commands.Commands and runs UpdateCheckCommand.RunAsync for update.check; and loading the server's commands.
        Commands = CommandRegistry.Load([UpdateCheckCommand.Command], []).Registry;
    }

    /// <summary>The head's local commands (currently only <c>update.check</c>), ready for a palette to list.</summary>
    public CommandRegistry Commands { get; }

        // The head owns a registry holding just its local update commands; update.restart is enabled only while a release is staged.
        // The palette lists the module's commands (LinuxHeadModule); this registry is what its enabled state is read from.
        RestartCommand = CreateRestartCommand(coordinator);
        Commands = CommandRegistry.Load([UpdateCheckCommand.Command, RestartCommand], []).Registry;
    }

    /// <summary>The head's local commands (<c>update.check</c> and <c>update.restart</c>), ready for a palette to list.</summary>
    public CommandRegistry Commands { get; }

    /// <summary>The <c>update.restart</c> palette entry; <see cref="Command.IsEnabled()"/> is true only while an update is staged.</summary>
    public Command RestartCommand { get; }

    /// <summary>The <c>update.restart</c> palette entry for <paramref name="coordinator"/>; enabled only while a release is staged.</summary>
    public static Command CreateRestartCommand(UpdateCoordinator coordinator)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        return new Command(
            UpdateCoordinator.RestartCommandId,
            "Restart to apply update",
            "apply update",
            ["update", "restart", "upgrade"],
            CommandSource.Local,
            () => coordinator.IsStaged);
    }

    /// <summary>
    /// Builds the restart action for the head. <paramref name="args"/> is the original command line; <paramref name="shutdown"/>
    /// shuts the application lifetime down cleanly. The new process starts through <see cref="PendingRestart"/> after <c>Main</c>
    /// has released the single-instance pipe.
    /// </summary>
    public UpdateRestart CreateRestart(IReadOnlyList<string> args, Action shutdown) =>
        new(() => Coordinator.IsStaged, () => Environment.ProcessPath, args, shutdown, PendingRestart.Set);

    /// <summary>
    /// Pass to <c>ChatHost.Start</c> as the welcome callback. Records the server's compatibility facts; until the first welcome
    /// arrives the updater fails closed and applies nothing.
    /// </summary>
    public void OnWelcome(Welcome welcome)
    {
        ArgumentNullException.ThrowIfNull(welcome);
        Compatibility.Record(welcome);
    }

    /// <summary>Creates the handler for <c>update.check</c>; <paramref name="owner"/> gives the window its notice belongs to.</summary>
    public UpdateCheckCommand CreateCheckCommand(Func<Window?> owner) => new(Coordinator, owner);

    /// <summary>The coordinator behind the command and the background check.</summary>
    public UpdateCoordinator Coordinator { get; }

    /// <summary>Feed this with each <c>Welcome</c> the connection receives; until it is fed, updates are only checked, never applied.</summary>
    public WelcomeCompatibilityHolder Compatibility { get; }

    /// <summary>Builds the host and starts the background check. Never throws; a problem becomes a "updates off" notice.</summary>
    public static UpdateHost Start(Func<string, string?> env, string homeDirectory, Action<string> log)
    {
        var host = Create(env, homeDirectory, log, TimeProvider.System, action => Dispatcher.UIThread.Post(action));
        _ = host.Coordinator.StartBackgroundCheck();
        return host;
    }

    /// <summary>Builds the host without starting anything.</summary>
    public static UpdateHost Create(Func<string, string?> env, string homeDirectory, Action<string> log, TimeProvider time, Action<Action> post)
    {
        ArgumentNullException.ThrowIfNull(env);
        ArgumentNullException.ThrowIfNull(log);
        var compat = new WelcomeCompatibilityHolder(RunningProtocol);
        try
        {
            var config = UpdateSettingsReader.Read(
                env,
                UpdatePaths.FromEnvironment(env, homeDirectory),
                new UpdateBuildIdentity(CompanyBuildIdentity.Address, CompanyBuildIdentity.KeyHashPin),
                InstalledVersion(),
                RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? ReleaseArtifact.ArchLinuxArm64 : ReleaseArtifact.ArchLinuxX64,
                File.Exists);
            if (!config.Enabled || !OperatingSystem.IsLinux())
            {
                string reason = config.DisabledReason ?? "The updater runs on Linux only.";
                log($"updates off: {reason}");
                return new UpdateHost(NewCoordinator(null, reason, compat, time, post, log), compat, null);
            }

            var endpoint = new ServerEndpoint(new Uri(config.ManifestUri!.GetLeftPart(UriPartial.Authority) + "/"), KeyHashPin: config.ServerKeyHashPin);
            var http = endpoint.CreateHttpClient();
            var updater = new LinuxUpdater(
                config.Settings!,
                new HttpReleaseSource(http, config.ManifestUri),
                new PhysicalUpdateFileSystem(),
                new SystemProcessRunner(),
                time,
                compat);
            return new UpdateHost(NewCoordinator(updater, null, compat, time, post, log), compat, http);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or UriFormatException)
        {
            log($"updates off: {ex.Message}");
            return new UpdateHost(NewCoordinator(null, $"Updates are off: {ex.Message}", compat, time, post, log), compat, null);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Coordinator.Dispose();
        _http?.Dispose();
    }

    private static UpdateCoordinator NewCoordinator(
        ILinuxUpdater? updater, string? reason, WelcomeCompatibilityHolder compat, TimeProvider time, Action<Action> post, Action<string> log) =>
        new(updater, reason, compat, time, _startDelay, post, log);

    private static string InstalledVersion()
    {
        string? info = typeof(UpdateHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        int plus = info?.IndexOf('+', StringComparison.Ordinal) ?? -1;
        return plus >= 0 ? info![..plus] : info ?? "0.0.0";
    }
}
