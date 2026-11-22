using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Bazaar.Server.BrowserPool.Abstractions;

namespace Bazaar.Server.BrowserPool.LinuxHost;

/// <summary>
/// <see cref="IPoolVmHost"/> for a Linux pool VM in which the Bazaar process itself runs as root. Every command is a
/// <see cref="System.Diagnostics.Process"/> started from an argument vector, with no shell. The firewall is applied with
/// <c>nft -f -</c> and then read back; anything short of the expected state throws <see cref="PoolFirewallException"/>, and
/// <see cref="StartAsync"/> refuses to launch unless the table, its drop rule and the session's own accept rule are in force, and then
/// unless the active probe (<see cref="FirewallProbe"/>) shows the slot really is blocked from the host and can still reach its proxy.
/// <para>
/// DevTools pipe (optional): when <see cref="LinuxPoolHostOptions.PoolSpawnHelperPath"/> is set, <see cref="StartAsync"/> makes two
/// FIFOs in a root-only directory, opens the host's ends, and puts <c>bazaar-pool-spawn</c> at the front of the launch argv. The tool
/// opens the FIFOs and execs the rest of the argv (systemd-run, nsenter, setpriv, bwrap, cage, Chromium) with the FIFOs on descriptors
/// 3 and 4, which is where <c>--remote-debugging-pipe</c> reads and writes. The host's ends are handed out by
/// <see cref="TryGetPipe"/>. Without the option nothing changes: a <see cref="System.Diagnostics.Process"/> cannot pass descriptors
/// itself, and the browser then has no usable pipe.
/// </para>
/// </summary>
public sealed class LinuxPoolVmHost : IPoolVmHost, IPoolDevToolsPipes
{
    private readonly IProcessRunner _runner;
    private readonly Func<IReadOnlyList<PoolFirewallRule>, string> _renderRuleset;
    private readonly LinuxPoolHostOptions _options;
    private readonly IFifoFactory _fifos;
    private readonly FirewallProbe _probe;
    private readonly ConcurrentDictionary<string, IRunningProcess> _running = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DevToolsPipeSet> _pipes = new(StringComparer.Ordinal);

    /// <summary>Creates the host.</summary>
    /// <param name="renderRuleset">Renders the complete nftables document for a rule set (the pool module's renderer). It must declare the table named in <paramref name="options"/>.</param>
    /// <param name="options">Settings; null uses the defaults.</param>
    /// <exception cref="PlatformNotSupportedException">The OS is not Linux.</exception>
    public LinuxPoolVmHost(Func<IReadOnlyList<PoolFirewallRule>, string> renderRuleset, LinuxPoolHostOptions? options = null)
        : this(new ProcessRunner(), renderRuleset, options, requireLinux: true, fifos: null)
    {
    }

    internal LinuxPoolVmHost(IProcessRunner runner, Func<IReadOnlyList<PoolFirewallRule>, string> renderRuleset, LinuxPoolHostOptions? options, bool requireLinux, IFifoFactory? fifos = null, IProbeListenerFactory? probeListeners = null)
    {
        if (requireLinux && !OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("LinuxPoolVmHost runs inside a Linux pool VM only.");
        }

        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(renderRuleset);
        _options = options ?? new LinuxPoolHostOptions();
        var errors = _options.Validate();
        if (errors.Count != 0)
        {
            throw new ArgumentException("Invalid pool host options: " + string.Join(' ', errors), nameof(options));
        }

        _runner = runner;
        _renderRuleset = renderRuleset;
        _fifos = fifos ?? new LibcFifoFactory();
        _probe = new FirewallProbe(runner, probeListeners ?? new TcpProbeListenerFactory(), _options);
    }

    /// <inheritdoc />
    public async Task RunAsync(PoolCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        var request = new ProcessRequest(command.Argv, null, _options.CommandTimeout, _options.MaxCapturedChars);
        var result = await _runner.RunAsync(request, ct).ConfigureAwait(false);
        var allowed = command.AllowedExitCodes ?? [0];
        if (!allowed.Contains(result.ExitCode))
        {
            throw new PoolCommandFailedException(command.Argv[0], result.ExitCode, result.StdErr.Trim());
        }
    }

    /// <summary>
    /// Runs every command in order, always all of them: a failure (including a cancelled or timed-out command) does not stop the
    /// rest. Use it for cleanup. Every failure is reported together.
    /// </summary>
    /// <exception cref="AggregateException">One or more commands failed; it holds each failure.</exception>
    public async Task RunAllAsync(IReadOnlyList<PoolCommand> commands, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(commands);
        List<Exception>? failures = null;
        foreach (var command in commands)
        {
            try
            {
                await RunAsync(command, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                (failures ??= []).Add(ex);
            }
        }

        if (failures is not null)
        {
            throw new AggregateException("Pool host commands failed: " + failures.Count.ToString(CultureInfo.InvariantCulture) + " of " + commands.Count.ToString(CultureInfo.InvariantCulture) + ".", failures);
        }
    }

    /// <inheritdoc />
    public async Task ApplyFirewallAsync(IReadOnlyList<PoolFirewallRule> rules, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rules);
        string document;
        try
        {
            document = _renderRuleset(rules);
        }
        catch (ArgumentException ex)
        {
            throw new PoolFirewallException("The firewall rules were refused by the renderer: " + ex.Message, ex);
        }

        if (string.IsNullOrWhiteSpace(document))
        {
            throw new PoolFirewallException("The firewall renderer produced no ruleset.");
        }

        // One nft transaction: it replaces the whole table or changes nothing, so no window without the drop rule opens.
        var apply = new ProcessRequest([_options.NftPath, "-f", "-"], document, _options.FirewallTimeout, _options.MaxCapturedChars);
        var result = await RunNftAsync(apply, "apply", ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new PoolFirewallException("nft refused the firewall ruleset (exit " + result.ExitCode.ToString(CultureInfo.InvariantCulture) + "): " + result.StdErr.Trim());
        }

        await VerifyAsync(rules, "after apply", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Confirms the table, its output chain and the slot-uid drop rule are still in force. Call it before each launch.
    /// </summary>
    /// <exception cref="PoolFirewallException">Something is missing or nft could not be asked.</exception>
    public Task CheckFirewallAsync(CancellationToken ct) => VerifyAsync([], "health check", ct);

    /// <inheritdoc />
    public async Task<PoolProcess> StartAsync(PoolLaunchSpec spec, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.Launch.Count == 0)
        {
            throw new ArgumentException("The launch command is empty.", nameof(spec));
        }

        // Fail closed: no table, no drop rule, or no accept for this session's own proxy means no browser.
        await VerifyAsync([spec.Firewall], "before launch", ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        // The listing only proves the rules are listed. Try it: the slot's uid, in its namespace, must be blocked from the host and reach its proxy.
        await _probe.RunAsync(spec, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        var argv = spec.Launch;
        DevToolsPipeSet? pipes = null;
        if (_options.PoolSpawnHelperPath is not null)
        {
            // The firewall check above has already passed. Everything below runs before any process exists, so a refusal costs nothing.
            if (_pipes.ContainsKey(spec.SessionId))
            {
                throw new PoolHostException("The session already has a DevTools pipe.");
            }

            var (commandsFifo, repliesFifo) = PoolPipePlan.FifoPaths(_options.PipeDirectory, spec.SessionId);
            if (PoolPipePlan.FindConflict(_options.PipeDirectory, spec.Launch) is not null)
            {
                throw new PoolHostException("The launch command names the DevTools pipe directory; the sandbox must not see it.");
            }

            argv = PoolPipePlan.Compose(_options.PoolSpawnHelperPath, commandsFifo, repliesFifo, spec.Launch, ResolveProgram);
            pipes = DevToolsPipeSet.Open(_fifos, _options.PipeDirectory, commandsFifo, repliesFifo);
        }

        IRunningProcess running;
        try
        {
            running = _runner.Start(new ProcessRequest(argv, null, Timeout.InfiniteTimeSpan, _options.MaxCapturedChars));
        }
        catch
        {
            pipes?.Dispose();
            throw;
        }

        var id = running.Id.ToString(CultureInfo.InvariantCulture);
        _running[id] = running;
        if (pipes is not null)
        {
            _pipes[spec.SessionId] = pipes;
        }

        var sessionId = spec.SessionId;
        _ = running.Exited.ContinueWith(
            _ =>
            {
                _running.TryRemove(new KeyValuePair<string, IRunningProcess>(id, running));
                if (pipes is not null)
                {
                    _pipes.TryRemove(new KeyValuePair<string, DevToolsPipeSet>(sessionId, pipes));
                    pipes.BrowserExited();
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return new PoolProcess(spec.SessionId, id);
    }

    /// <inheritdoc />
    public async Task StopAsync(PoolProcess process, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (!_running.TryGetValue(process.GuestProcessId, out var running))
        {
            // Already exited and removed, or never started here: stopping is idempotent.
            DisposePipe(process.SessionId);
            return;
        }

        running.KillTree();
        try
        {
            await running.Exited.WaitAsync(_options.StopTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new PoolHostException("The browser process did not exit after it was killed.", ex);
        }

        _running.TryRemove(new KeyValuePair<string, IRunningProcess>(process.GuestProcessId, running));
        DisposePipe(process.SessionId);
        await running.DisposeAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public bool TryGetPipe(string sessionId, [NotNullWhen(true)] out PoolDevToolsPipe? pipe)
    {
        if (sessionId is not null && _pipes.TryGetValue(sessionId, out var set))
        {
            pipe = set.Pipe;
            return true;
        }

        pipe = null;
        return false;
    }

    /// <summary>
    /// Observes a started browser: completes with its exit code when it ends. A process this host does not know (it already exited
    /// and was released, or came from elsewhere) throws.
    /// </summary>
    /// <exception cref="PoolHostException">The process is not known to this host.</exception>
    public async Task<int> WaitForExitAsync(PoolProcess process, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (!_running.TryGetValue(process.GuestProcessId, out var running))
        {
            throw new PoolHostException("The process is not running under this host.");
        }

        return await running.Exited.WaitAsync(ct).ConfigureAwait(false);
    }

    private static string? ResolveProgram(string name) =>
        PoolPipePlan.ResolveOnPath(name, Environment.GetEnvironmentVariable("PATH"), File.Exists);

    private void DisposePipe(string sessionId)
    {
        if (_pipes.TryRemove(sessionId, out var set))
        {
            set.Dispose();
        }
    }

    private async Task VerifyAsync(IReadOnlyList<PoolFirewallRule> expected, string stage, CancellationToken ct)
    {
        var list = new ProcessRequest([_options.NftPath, "list", "table", "inet", _options.TableName], null, _options.FirewallTimeout, _options.MaxCapturedChars * 8);
        var result = await RunNftAsync(list, "verify " + stage, ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new PoolFirewallException("The firewall table could not be read (" + stage + ", exit " + result.ExitCode.ToString(CultureInfo.InvariantCulture) + "): " + result.StdErr.Trim());
        }

        var problems = NftListingCheck.Problems(result.StdOut, _options.TableName, expected, _options.SlotInterfacePrefix);
        if (problems.Count != 0)
        {
            throw new PoolFirewallException("The firewall is not in the expected state (" + stage + "): " + string.Join("; ", problems) + ".");
        }
    }

    private async Task<ProcessResult> RunNftAsync(ProcessRequest request, string what, CancellationToken ct)
    {
        try
        {
            return await _runner.RunAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
        {
            throw new PoolFirewallException("nft could not be run (" + what + "): " + ex.Message, ex);
        }
    }
}
