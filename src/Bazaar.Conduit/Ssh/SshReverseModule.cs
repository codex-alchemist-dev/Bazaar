using System.ComponentModel;
using System.Globalization;

namespace Bazaar.Conduit.Ssh;

/// <summary>
/// SSH reverse tunnel (doc 39 section 2, priority 75, off by default). Runs <c>ssh -N -R</c> from this host to an
/// operator-controlled jump host, so a port on the jump host forwards to the server's local listener. The jump host
/// operator controls the host key and the account. Arguments go through an argument list, never a shell, and the
/// jump host and user names are checked against a strict character set before any process starts.
/// </summary>
public sealed class SshReverseModule : IConduitModule
{
    private const string _defaultBinary = "ssh";
    private const int _defaultJumpPort = 22;
    private const int _maxPort = 65535;
    private const int _maxHostLength = 253;
    private const int _maxUserLength = 32;

    private static readonly TimeSpan _readyWindow = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan _initialBackoff = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan _maxBackoff = TimeSpan.FromMinutes(5);

    private readonly ISshProcessLauncher _launcher;
    private readonly Lock _gate = new();
    private ModuleStatus _status;
    private CancellationTokenSource? _lifetime;
    private Task? _supervisor;
    private Attempt? _current;

    /// <summary>Creates the module over a launcher. Use <see cref="SystemSshProcessLauncher"/> in production.</summary>
    public SshReverseModule(ISshProcessLauncher launcher)
    {
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _status = new ModuleStatus(Id, ConduitModuleState.Disabled, null, []);
    }

    /// <inheritdoc />
    public string Id => "ssh";

    /// <inheritdoc />
    public string DisplayName => "SSH Reverse Tunnel";

    /// <inheritdoc />
    public ConduitScope Scope => ConduitScope.Internet;

    /// <inheritdoc />
    public int Priority => 75;

    /// <inheritdoc />
    public bool DefaultEnabled => false;

    /// <inheritdoc />
    public ModuleStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    /// <inheritdoc />
    public event EventHandler<ModuleStatus>? StatusChanged;

    /// <inheritdoc />
    /// <remarks>
    /// Launch errors are reported through <see cref="Status"/>, never thrown. The module keeps running until
    /// <see cref="StopAsync"/>; the startup token only covers this call.
    /// </remarks>
    public ValueTask StartAsync(ConduitContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var plan = Resolve(context.Settings, context.LocalPort, out var failure);
        if (plan is null)
        {
            lock (_gate)
            {
                if (_lifetime is null)
                {
                    TransitionLocked(ConduitModuleState.Failed, failure, []);
                }
            }

            return ValueTask.CompletedTask;
        }

        var lifetime = new CancellationTokenSource();
        lock (_gate)
        {
            if (_lifetime is not null)
            {
                lifetime.Dispose();
                return ValueTask.CompletedTask;
            }

            _lifetime = lifetime;
            TransitionLocked(ConduitModuleState.Starting, null, []);
        }

        Attempt first;
        try
        {
            first = Launch(context, plan, lifetime.Token);
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _lifetime = null;
            }

            lifetime.Cancel();
            ReportLaunchFailure(context, ex);
            return ValueTask.CompletedTask;
        }

        _supervisor = SuperviseAsync(context, plan, first, lifetime.Token);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>Kills the child process, withdraws the offer and waits for the supervisor to finish.</remarks>
    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        CancellationTokenSource? lifetime;
        Task? supervisor;
        Attempt? attempt;
        lock (_gate)
        {
            lifetime = _lifetime;
            supervisor = _supervisor;
            attempt = _current;
            _lifetime = null;
            _supervisor = null;
            _current = null;
        }

        if (lifetime is not null)
        {
            lifetime.Cancel();
            attempt?.Process.Kill();
        }

        if (supervisor is not null)
        {
            try
            {
                await supervisor.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The caller's deadline passed; the process was already killed above.
            }
        }

        lock (_gate)
        {
            TransitionLocked(ConduitModuleState.Disabled, null, []);
        }
    }

    /// <summary>Validates the settings and builds the ssh command. Returns null with a reason when the module must not start.</summary>
    private static Plan? Resolve(IConduitSettings settings, int localPort, out string? failure)
    {
        failure = null;

        var host = settings.Read("conduit.ssh.jump_host", string.Empty);
        if (string.IsNullOrWhiteSpace(host))
        {
            failure = "not-configured";
            return null;
        }

        if (!IsValidHost(host))
        {
            failure = "invalid-host";
            return null;
        }

        var user = settings.Read("conduit.ssh.jump_user", string.Empty);
        if (string.IsNullOrWhiteSpace(user))
        {
            failure = "not-configured";
            return null;
        }

        if (!IsValidUser(user))
        {
            failure = "invalid-user";
            return null;
        }

        var remotePort = settings.Read("conduit.ssh.remote_port", 0);
        if (remotePort is < 1 or > _maxPort)
        {
            failure = "invalid-remote-port";
            return null;
        }

        var jumpPort = settings.Read("conduit.ssh.jump_port", _defaultJumpPort);
        if (jumpPort is < 1 or > _maxPort)
        {
            failure = "invalid-jump-port";
            return null;
        }

        var identity = settings.Read("conduit.ssh.identity_file", string.Empty);
        var hasIdentity = !string.IsNullOrWhiteSpace(identity);
        if (hasIdentity && identity.Any(char.IsControl))
        {
            failure = "invalid-identity-file";
            return null;
        }

        var binary = settings.Read("conduit.ssh.binary_path", _defaultBinary);
        if (string.IsNullOrWhiteSpace(binary))
        {
            binary = _defaultBinary;
        }

        var arguments = new List<string>
        {
            "-N",
            "-R", FormattableString.Invariant($"{remotePort}:127.0.0.1:{localPort}"),
            "-p", jumpPort.ToString(CultureInfo.InvariantCulture),
            "-o", "ExitOnForwardFailure=yes",
            "-o", "ServerAliveInterval=30",
            "-o", "BatchMode=yes",
            "-o", "StrictHostKeyChecking=yes",
        };

        if (hasIdentity)
        {
            arguments.Add("-i");
            arguments.Add(identity);
        }

        // The destination is last. It starts with the validated user name, so it can never be read as an option.
        arguments.Add($"{user}@{host}");

        return new Plan(binary, host, remotePort, arguments);
    }

    /// <summary>A bare DNS name or IPv4 address: letters, digits, dots and hyphens only, so no shell or option syntax can pass.</summary>
    private static bool IsValidHost(string host)
    {
        if (host.Length > _maxHostLength || host[0] == '-')
        {
            return false;
        }

        if (!host.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-'))
        {
            return false;
        }

        return Uri.CheckHostName(host) is UriHostNameType.Dns or UriHostNameType.IPv4;
    }

    /// <summary>A plain account name: starts with a letter, digit or underscore, then letters, digits, dots, hyphens or underscores.</summary>
    private static bool IsValidUser(string user)
    {
        if (user.Length > _maxUserLength || !(char.IsAsciiLetterOrDigit(user[0]) || user[0] == '_'))
        {
            return false;
        }

        return user.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');
    }

    /// <summary>Starts one ssh process and its readers. Throws when the launcher cannot start it.</summary>
    private Attempt Launch(ConduitContext context, Plan plan, CancellationToken lifetime)
    {
        // The ready timer is created before the process starts, so a test clock advanced after the start sees it.
        var timers = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        var ready = Task.Delay(_readyWindow, context.Time, timers.Token);

        ISshProcess process;
        try
        {
            process = _launcher.Start(plan.FileName, plan.Arguments);
        }
        catch (Exception)
        {
            timers.Cancel();
            throw;
        }

        var attempt = new Attempt(process, timers, ready, context, plan);
        var killNow = false;
        lock (_gate)
        {
            _current = attempt;
            killNow = lifetime.IsCancellationRequested;
        }

        if (killNow)
        {
            process.Kill();
        }

        // Standard error is drained but never logged: it can name the identity file.
        _ = DrainAsync(process.StandardErrorLines);
        _ = PublishWhenReadyAsync(attempt);
        return attempt;
    }

    private async Task SuperviseAsync(ConduitContext context, Plan plan, Attempt first, CancellationToken lifetime)
    {
        var attempt = first;
        var failures = 0;
        try
        {
            while (true)
            {
                var exitCode = await attempt.Process.Exited.ConfigureAwait(false);
                var wasStable = attempt.Ready.IsCompletedSuccessfully;
                lock (_gate)
                {
                    attempt.Ended = true;
                }

                attempt.Timers.Cancel();
                if (lifetime.IsCancellationRequested)
                {
                    return;
                }

                failures = wasStable ? 1 : failures + 1;
                var delay = BackoffFor(failures);

                // The backoff timer is created before the status changes, so a test clock advanced on Degraded sees it.
                var backoff = Task.Delay(delay, context.Time, lifetime);
                SshLog.ProcessExited(context.Logger, exitCode, delay.TotalSeconds);
                lock (_gate)
                {
                    TransitionLocked(ConduitModuleState.Degraded, "ssh-exited", []);
                }

                await backoff.ConfigureAwait(false);

                lock (_gate)
                {
                    TransitionLocked(ConduitModuleState.Starting, null, []);
                }

                try
                {
                    attempt = Launch(context, plan, lifetime);
                }
                catch (Exception ex)
                {
                    ReportLaunchFailure(context, ex);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            // Stopped; StopAsync has already killed the process.
        }
    }

    private static async Task DrainAsync(IAsyncEnumerable<string> lines)
    {
        await foreach (var line in lines)
        {
            _ = line;
        }
    }

    private async Task PublishWhenReadyAsync(Attempt attempt)
    {
        try
        {
            await attempt.Ready.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        Publish(attempt);
    }

    private void Publish(Attempt attempt)
    {
        var uri = new Uri($"wss://{attempt.Plan.Host}:{attempt.Plan.RemotePort.ToString(CultureInfo.InvariantCulture)}/ws");
        lock (_gate)
        {
            if (attempt.Published || attempt.Ended || attempt.Timers.IsCancellationRequested)
            {
                return;
            }

            attempt.Published = true;
            var offer = new ConduitOffer(
                Id,
                Scope,
                uri,
                attempt.Context.ServerKeyHash,
                ExpiresAt: null,
                RequiresRelay: false,
                Verified: false,
                TerminatesTls: false);
            TransitionLocked(ConduitModuleState.Running, null, [offer]);
        }
    }

    private void ReportLaunchFailure(ConduitContext context, Exception ex)
    {
        var reason = ClassifyLaunchFailure(ex);
        SshLog.LaunchFailed(context.Logger, reason);
        lock (_gate)
        {
            TransitionLocked(ConduitModuleState.Failed, reason, []);
        }
    }

    /// <summary>Requires <see cref="_gate"/> to be held. Raises <see cref="StatusChanged"/> while held, so events keep order.</summary>
    private void TransitionLocked(ConduitModuleState state, string? reason, IReadOnlyList<ConduitOffer> offers)
    {
        _status = new ModuleStatus(Id, state, reason, offers);
        StatusChanged?.Invoke(this, _status);
    }

    private static string ClassifyLaunchFailure(Exception ex) => ex switch
    {
        FileNotFoundException => "binary-not-found",
        Win32Exception { NativeErrorCode: 2 or 3 } => "binary-not-found",
        _ => "launch-failed",
    };

    private static TimeSpan BackoffFor(int failures)
    {
        var exponent = Math.Min(Math.Max(failures, 1) - 1, 10);
        var seconds = Math.Min(_maxBackoff.TotalSeconds, _initialBackoff.TotalSeconds * Math.Pow(2, exponent));
        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>Validated launch settings for one module start.</summary>
    private sealed record Plan(string FileName, string Host, int RemotePort, IReadOnlyList<string> Arguments);

    /// <summary>One ssh process and the state that belongs to it.</summary>
    private sealed class Attempt(
        ISshProcess process,
        CancellationTokenSource timers,
        Task ready,
        ConduitContext context,
        Plan plan)
    {
        public ISshProcess Process { get; } = process;

        /// <summary>Cancels the ready timer of this attempt.</summary>
        public CancellationTokenSource Timers { get; } = timers;

        /// <summary>Completes when the process has run for the ready window.</summary>
        public Task Ready { get; } = ready;

        public ConduitContext Context { get; } = context;

        public Plan Plan { get; } = plan;

        /// <summary>True once an offer was published for this process.</summary>
        public bool Published { get; set; }

        /// <summary>True once the process has exited or been stopped.</summary>
        public bool Ended { get; set; }
    }
}
