using System.ComponentModel;

namespace Bazaar.Conduit.Cloudflared;

/// <summary>
/// Cloudflare Tunnel transport (doc 39 section 2, priority 30, off by default). Runs <c>cloudflared</c> as a child
/// process: <c>tunnel run</c> for a named tunnel, or <c>tunnel --url</c> for a quick tunnel whose address changes on
/// every start. Cloudflare terminates TLS on this path, so every offer sets <see cref="ConduitOffer.TerminatesTls"/>
/// and carries the server key hash (doc 39 section 3). The tunnel token goes in the environment, never on the command line.
/// </summary>
public sealed class CloudflaredModule : IConduitModule
{
    private const string _modeQuick = "quick";
    private const string _modeNamed = "named";
    private const string _defaultBinary = "cloudflared";
    private const string _registeredLine = "Registered tunnel connection";
    private const string _tokenVariable = "TUNNEL_TOKEN";

    private static readonly TimeSpan _readyWindow = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan _initialBackoff = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan _maxBackoff = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan _stableRun = TimeSpan.FromMinutes(5);

    private readonly ICloudflaredLauncher _launcher;
    private readonly Lock _gate = new();
    private ModuleStatus _status;
    private CancellationTokenSource? _lifetime;
    private Task? _supervisor;
    private Attempt? _current;

    /// <summary>Creates the module over a launcher. Use <see cref="SystemCloudflaredLauncher"/> in production.</summary>
    public CloudflaredModule(ICloudflaredLauncher launcher)
    {
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _status = new ModuleStatus(Id, ConduitModuleState.Disabled, null, []);
    }

    /// <inheritdoc />
    public string Id => "cloudflared";

    /// <inheritdoc />
    public string DisplayName => "Cloudflare Tunnel";

    /// <inheritdoc />
    public ConduitScope Scope => ConduitScope.Internet;

    /// <inheritdoc />
    public int Priority => 30;

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
    /// <remarks>Kills the child process, withdraws every offer and waits for the supervisor to finish.</remarks>
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

    private static Plan? Resolve(IConduitSettings settings, int localPort, out string? failure)
    {
        failure = null;
        var mode = settings.Read("conduit.cloudflared.mode", _modeNamed);
        var binary = settings.Read("conduit.cloudflared.binary_path", _defaultBinary);
        if (string.IsNullOrWhiteSpace(binary))
        {
            binary = _defaultBinary;
        }

        if (string.Equals(mode, _modeQuick, StringComparison.Ordinal))
        {
            var arguments = new[] { "tunnel", "--url", $"http://127.0.0.1:{localPort}" };
            return new Plan(false, binary, null, arguments, new Dictionary<string, string>());
        }

        if (!string.Equals(mode, _modeNamed, StringComparison.Ordinal))
        {
            failure = "invalid-mode";
            return null;
        }

        var hostname = settings.Read("conduit.cloudflared.hostname", string.Empty);
        if (string.IsNullOrWhiteSpace(hostname))
        {
            failure = "not-configured";
            return null;
        }

        if (Uri.CheckHostName(hostname) != UriHostNameType.Dns)
        {
            failure = "invalid-hostname";
            return null;
        }

        var token = settings.Read("conduit.cloudflared.tunnel_token", string.Empty);
        if (string.IsNullOrWhiteSpace(token))
        {
            failure = "not-configured";
            return null;
        }

        var named = new[] { "tunnel", "run" };
        return new Plan(true, binary, hostname, named, new Dictionary<string, string> { [_tokenVariable] = token });
    }

    /// <summary>Starts one child process and its readers. Throws when the launcher cannot start it.</summary>
    private Attempt Launch(ConduitContext context, Plan plan, CancellationToken lifetime)
    {
        // Timers are created before the process starts, so a test clock advanced after the start sees them.
        var timers = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        var stable = Task.Delay(_stableRun, context.Time, timers.Token);
        var ready = plan.Named ? Task.Delay(_readyWindow, context.Time, timers.Token) : null;

        ICloudflaredProcess process;
        var started = false;
        try
        {
            process = _launcher.Start(plan.FileName, plan.Arguments, plan.Environment);
            started = true;
        }
        finally
        {
            if (!started)
            {
                timers.Cancel();
            }
        }

        var attempt = new Attempt(process, timers, stable, context, plan);
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

        _ = ReadLinesAsync(attempt, process.StandardOutputLines);
        _ = ReadLinesAsync(attempt, process.StandardErrorLines);
        if (ready is not null)
        {
            _ = PublishWhenReadyAsync(attempt, ready);
        }

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
                var wasStable = attempt.Stable.IsCompletedSuccessfully;
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
                CloudflaredLog.ProcessExited(context.Logger, exitCode, delay.TotalSeconds);
                lock (_gate)
                {
                    TransitionLocked(ConduitModuleState.Degraded, "process-exited", []);
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

    private async Task ReadLinesAsync(Attempt attempt, IAsyncEnumerable<string> lines)
    {
        await foreach (var line in lines)
        {
            OnLine(attempt, line);
        }
    }

    private async Task PublishWhenReadyAsync(Attempt attempt, Task ready)
    {
        try
        {
            await ready.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        Publish(attempt, attempt.Plan.Hostname!);
    }

    private void OnLine(Attempt attempt, string line)
    {
        if (attempt.Published)
        {
            return;
        }

        if (attempt.Plan.Named)
        {
            if (line.Contains(_registeredLine, StringComparison.Ordinal))
            {
                Publish(attempt, attempt.Plan.Hostname!);
            }

            return;
        }

        var host = CloudflaredUrlParser.TryParseQuickTunnelHost(line);
        if (host is not null)
        {
            Publish(attempt, host);
        }
    }

    private void Publish(Attempt attempt, string hostname)
    {
        var uri = new Uri($"wss://{hostname}/ws");
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
                TerminatesTls: true);
            TransitionLocked(ConduitModuleState.Running, null, [offer]);
        }
    }

    private void ReportLaunchFailure(ConduitContext context, Exception ex)
    {
        var reason = ClassifyLaunchFailure(ex);
        CloudflaredLog.LaunchFailed(context.Logger, reason);
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
        _ => ex.GetType().Name,
    };

    private static TimeSpan BackoffFor(int failures)
    {
        var exponent = Math.Min(Math.Max(failures, 1) - 1, 10);
        var seconds = Math.Min(_maxBackoff.TotalSeconds, _initialBackoff.TotalSeconds * Math.Pow(2, exponent));
        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>Resolved launch settings for one module start.</summary>
    private sealed record Plan(
        bool Named,
        string FileName,
        string? Hostname,
        IReadOnlyList<string> Arguments,
        IReadOnlyDictionary<string, string> Environment);

    /// <summary>One child process and the state that belongs to it.</summary>
    private sealed class Attempt(
        ICloudflaredProcess process,
        CancellationTokenSource timers,
        Task stable,
        ConduitContext context,
        Plan plan)
    {
        public ICloudflaredProcess Process { get; } = process;

        /// <summary>Cancels the ready and stable timers of this attempt.</summary>
        public CancellationTokenSource Timers { get; } = timers;

        /// <summary>Completes when the process has run for the stable window.</summary>
        public Task Stable { get; } = stable;

        public ConduitContext Context { get; } = context;

        public Plan Plan { get; } = plan;

        /// <summary>True once an offer was published for this process.</summary>
        public bool Published { get; set; }

        /// <summary>True once the process has exited or been stopped.</summary>
        public bool Ended { get; set; }
    }
}
