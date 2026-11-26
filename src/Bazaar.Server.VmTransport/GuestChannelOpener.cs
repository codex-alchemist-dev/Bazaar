using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.VmTransport;

/// <summary>
/// Limits of the guest channel opener (doc 16 "Channel opener", PROPOSED). Constructor options with shipped defaults: no setting contribution exists
/// for them yet.
/// </summary>
public sealed record GuestChannelOpenerOptions
{
    /// <summary>How often the opener asks which VMs are running.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Random spread applied to the poll interval and to every back-off, as a fraction (0.2 is plus or minus 20 percent). 0 turns it off.</summary>
    public double Jitter { get; init; } = 0.2;

    /// <summary>Open attempts that may be waiting or checking a Hello at once. A VM whose guest has not connected holds one until <see cref="AttemptTimeout"/>.</summary>
    public int MaxConcurrentOpens { get; init; } = 4;

    /// <summary>How long one attempt waits for the guest to connect and say Hello. A timeout is not a failure: the VM is tried again at the next poll.</summary>
    public TimeSpan AttemptTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>The wait after the first failure (and after a channel ends). It doubles with every further failure of the same VM.</summary>
    public TimeSpan InitialBackoff { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The longest wait between attempts for one VM.</summary>
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The wait after the first rejected Hello (wrong VM id or nonce). It doubles up to <see cref="MaxBackoff"/>. Longer than <see cref="InitialBackoff"/>
    /// so a hostile guest that keeps dialling in cannot make the host spin; the listener closes anything that arrives while no attempt is waiting.
    /// </summary>
    public TimeSpan BadHelloBackoff { get; init; } = TimeSpan.FromSeconds(5);

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(PollInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxConcurrentOpens, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(AttemptTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(InitialBackoff, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxBackoff, InitialBackoff);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(BadHelloBackoff, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(Jitter, 0.0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(Jitter, 1.0);
    }
}

/// <summary>
/// Opens the guest channel of every running VM and keeps it open (doc 16 "Channel opener", PROPOSED). Once a VM is running it waits for the guest to
/// dial in, checks the Hello against the VM id and the server-issued nonce, registers the channel in the directory, and opens again after the
/// channel is lost while the VM still runs.
/// </summary>
/// <remarks>
/// <para>
/// The channel is trusted only through <see cref="GuestChannelFactory.OpenAsync"/> (the peer id comes from the hypervisor and the Hello must carry the
/// nonce) and then <see cref="GuestChannelRegistrar.RegisterOpenedAsync"/> (the VM store must say the VM is a live VM of that agent). A guest that
/// fails either is dropped and nothing is registered. The nonce is read from <see cref="IGuestOpenTargets"/> at the start of each attempt, handed to
/// the factory and nowhere else: it is not logged, not kept in state, and not part of any exception this class throws.
/// </para>
/// <para>
/// At most one attempt per VM is in flight, and at most <see cref="GuestChannelOpenerOptions.MaxConcurrentOpens"/> overall. A failed attempt backs off
/// exponentially (reset by a success), a rejected Hello starts from a longer base. A VM that stops or retires has its attempt cancelled and its
/// channel disposed. The host OS picks the transport (Hyper-V sockets on Windows, vsock on Linux); the guest OS only decides what the directory records.
/// </para>
/// </remarks>
public sealed partial class GuestChannelOpener : BackgroundService
{
    private readonly IGuestOpenTargets _targets;
    private readonly GuestChannelFactory _factory;
    private readonly IGuestConnectionListener _listener;
    private readonly GuestChannelRegistrar _registrar;
    private readonly GuestChannelRegistry _registry;
    private readonly GuestChannelOpenerOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<GuestChannelOpener> _logger;
    private readonly IGuestPeerIdResolver? _peers;
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, VmWork> _work = [];

    /// <summary>Creates the opener. <paramref name="peers"/> may be null: the backend ref is then the peer id.</summary>
    public GuestChannelOpener(
        IGuestOpenTargets targets,
        GuestChannelFactory factory,
        IGuestConnectionListener listener,
        GuestChannelRegistrar registrar,
        GuestChannelRegistry registry,
        GuestChannelOpenerOptions options,
        TimeProvider time,
        ILogger<GuestChannelOpener> logger,
        IGuestPeerIdResolver? peers = null)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(listener);
        ArgumentNullException.ThrowIfNull(registrar);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        options.Validate();
        _targets = targets;
        _factory = factory;
        _listener = listener;
        _registrar = registrar;
        _registry = registry;
        _options = options;
        _time = time;
        _logger = logger;
        _peers = peers;
    }

    /// <summary>Attempts in flight now.</summary>
    internal int ActiveAttempts
    {
        get
        {
            lock (_gate)
            {
                return _work.Values.Count(static w => w.InFlight);
            }
        }
    }

    /// <summary>
    /// Reads the running VMs once: releases the ones that stopped and starts attempts for the ones that have no live channel, are not backing off and
    /// fit under the concurrency limit. Returns without waiting for the attempts. The hosted loop calls it every poll interval.
    /// </summary>
    public async Task PollOnceAsync(CancellationToken ct)
    {
        IReadOnlyList<GuestOpenTarget> running;
        try
        {
            running = await _targets.ListRunningAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Not knowing which VMs run is not the same as none running: keep what is in flight and ask again.
            LogListFailed(_logger, ex.GetType().Name);
            return;
        }

        var now = _time.GetUtcNow();
        var runningIds = new HashSet<Guid>(running.Count);
        foreach (var target in running)
        {
            runningIds.Add(target.VmId);
        }

        List<VmWork> gone;
        lock (_gate)
        {
            gone = [.. _work.Values.Where(w => !runningIds.Contains(w.VmId))];
            foreach (var work in gone)
            {
                _work.Remove(work.VmId);
            }
        }

        foreach (var work in gone)
        {
            Release(work);
            LogReleased(_logger, work.VmId);
        }

        var due = new List<(GuestOpenTarget Target, VmWork Work)>();
        var seen = new HashSet<Guid>();
        foreach (var target in running)
        {
            if (!seen.Add(target.VmId))
            {
                continue;
            }

            if (!IsUsable(target))
            {
                WarnInvalidOnce(target.VmId);
                continue;
            }

            var work = WorkFor(target.VmId);
            lock (_gate)
            {
                if (work.InFlight || now < work.NotBefore)
                {
                    continue;
                }
            }

            if (_registry.Find(target.VmId) is not null)
            {
                continue;
            }

            due.Add((target, work));
        }

        // The VM that has waited longest goes first, so VMs whose guests are slow to boot rotate through a small limit.
        due.Sort(static (a, b) => a.Work.LastEnded.CompareTo(b.Work.LastEnded));
        foreach (var (target, work) in due)
        {
            lock (_gate)
            {
                if (_work.Values.Count(static w => w.InFlight) >= _options.MaxConcurrentOpens)
                {
                    break;
                }

                if (work.InFlight || !_work.ContainsKey(work.VmId))
                {
                    continue;
                }

                var cts = new CancellationTokenSource();
                work.Cts = cts;
                work.Attempt = Task.Run(() => RunAttemptAsync(work, target.VmId, cts), CancellationToken.None);
            }
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(Jittered(_options.PollInterval, _options.Jitter), _time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // The loop must outlive any one bad poll.
                LogPollFailed(_logger, ex.GetType().Name);
                try
                {
                    await Task.Delay(_options.PollInterval, _time, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        List<VmWork> all;
        lock (_gate)
        {
            all = [.. _work.Values];
            _work.Clear();
        }

        foreach (var work in all)
        {
            Release(work);
        }

        var attempts = all.Select(static w => w.Attempt).OfType<Task>().ToArray();
        try
        {
            await Task.WhenAll(attempts).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Attempts end themselves and swallow their own failures; a host that stops waiting just moves on.
        }
    }

    private async Task RunAttemptAsync(VmWork work, Guid vmId, CancellationTokenSource owner)
    {
        try
        {
            var stop = owner.Token;
            using var timeout = new CancellationTokenSource(_options.AttemptTimeout, _time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop, timeout.Token);
            try
            {
                // Read the target again: the VM may have stopped since the poll, and the nonce is read as late as possible.
                var target = await _targets.GetAsync(vmId, linked.Token).ConfigureAwait(false);
                if (target is null || !IsUsable(target) || target.VmId != vmId)
                {
                    Touch(work);
                    return;
                }

                var peerId = _peers is null
                    ? target.BackendRef
                    : await _peers.ResolveAsync(target, linked.Token).ConfigureAwait(false);
                if (string.IsNullOrEmpty(peerId))
                {
                    throw new InvalidOperationException("The hypervisor does not know the VM yet.");
                }

                var handshake = new GuestHandshake(target.VmId.ToString("D"), target.Nonce);
                var channel = await _factory.OpenAsync(_listener, peerId, handshake, linked.Token).ConfigureAwait(false);

                // The registrar disposes the channel on every refusal. It checks the VM store; the name check below is a second look at what it recorded.
                var guest = await _registrar.RegisterOpenedAsync(target.VmId, target.AgentId, handshake, channel, linked.Token).ConfigureAwait(false);
                if (!string.Equals(guest.VmName, target.BackendRef, StringComparison.Ordinal) || stop.IsCancellationRequested)
                {
                    _registry.Unregister(target.VmId, channel);
                    await DisposeQuietlyAsync(channel).ConfigureAwait(false);
                    if (stop.IsCancellationRequested)
                    {
                        return;
                    }

                    throw new GuestChannelRegistrationException("The VM store names a different VM than the open target.");
                }

                OnOpened(work, channel);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                // The VM stopped or retired, or the host is stopping.
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                // The guest did not dial in yet. Not a failure: try again at the next poll.
                LogWaiting(_logger, vmId);
                Touch(work);
            }
            catch (GuestHandshakeException)
            {
                // The exception text is not logged: it may quote a frame the guest sent.
                Fail(work, badHello: true, vmId, errorType: nameof(GuestHandshakeException));
            }
            catch (GuestChannelRegistrationException)
            {
                Fail(work, badHello: false, vmId, errorType: nameof(GuestChannelRegistrationException));
            }
            catch (Exception ex)
            {
                Fail(work, badHello: false, vmId, ex.GetType().Name);
            }
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(work.Cts, owner))
                {
                    work.Cts = null;
                }
            }

            owner.Dispose();
        }
    }

    private void OnOpened(VmWork work, GuestChannel channel)
    {
        lock (_gate)
        {
            work.Failures = 0;
            work.Channel = channel;
            work.NotBefore = default;
            work.LastEnded = _time.GetUtcNow();
        }

        LogOpened(_logger, work.VmId);
        _ = channel.Completion.ContinueWith(
            _ =>
            {
                lock (_gate)
                {
                    if (ReferenceEquals(work.Channel, channel))
                    {
                        work.Channel = null;
                        work.LastEnded = _time.GetUtcNow();
                        work.NotBefore = work.LastEnded + _options.InitialBackoff;
                    }
                }

                LogChannelEnded(_logger, work.VmId);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void Fail(VmWork work, bool badHello, Guid vmId, string errorType)
    {
        TimeSpan delay;
        lock (_gate)
        {
            work.Failures = Math.Min(work.Failures + 1, 30);
            var baseDelay = badHello ? _options.BadHelloBackoff : _options.InitialBackoff;
            var scaled = TimeSpan.FromTicks((long)Math.Min(baseDelay.Ticks * Math.Pow(2, work.Failures - 1), _options.MaxBackoff.Ticks));
            delay = Jittered(scaled, _options.Jitter);
            if (delay > _options.MaxBackoff)
            {
                delay = _options.MaxBackoff;
            }

            work.LastEnded = _time.GetUtcNow();
            work.NotBefore = work.LastEnded + delay;
        }

        var seconds = (int)Math.Ceiling(delay.TotalSeconds);
        if (badHello)
        {
            LogBadHello(_logger, vmId, seconds);
        }
        else
        {
            LogFailed(_logger, vmId, errorType, seconds);
        }
    }

    /// <summary>Records that an attempt ended without a failure, so the VM goes to the back of the queue.</summary>
    private void Touch(VmWork work)
    {
        lock (_gate)
        {
            work.LastEnded = _time.GetUtcNow();
        }
    }

    private VmWork WorkFor(Guid vmId)
    {
        lock (_gate)
        {
            if (!_work.TryGetValue(vmId, out var work))
            {
                work = new VmWork(vmId);
                _work[vmId] = work;
            }

            return work;
        }
    }

    private void WarnInvalidOnce(Guid vmId)
    {
        var work = WorkFor(vmId);
        bool first;
        lock (_gate)
        {
            first = !work.WarnedInvalid;
            work.WarnedInvalid = true;
        }

        if (first)
        {
            LogInvalidTarget(_logger, vmId);
        }
    }

    private static bool IsUsable(GuestOpenTarget target) =>
        target.VmId != Guid.Empty
        && GuestChannelRegistry.TryParseAgentId(target.AgentId, out _)
        && GuestChannelRegistry.IsValidVmName(target.BackendRef)
        && Enum.IsDefined(target.Os)
        && !string.IsNullOrEmpty(target.Nonce);

    private void Release(VmWork work)
    {
        CancellationTokenSource? cts;
        GuestChannel? channel;
        lock (_gate)
        {
            cts = work.Cts;
            channel = work.Channel;
            work.Channel = null;
        }

        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The attempt had already ended.
        }

        if (channel is not null)
        {
            _ = DisposeQuietlyAsync(channel);
        }
    }

    private static async Task DisposeQuietlyAsync(GuestChannel channel)
    {
        try
        {
            await channel.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Being thrown away; a failed close changes nothing.
        }
    }

    private static TimeSpan Jittered(TimeSpan value, double fraction) =>
        fraction <= 0 ? value : value * (1.0 + ((Random.Shared.NextDouble() * 2.0) - 1.0) * fraction);

    [LoggerMessage(Level = LogLevel.Information, Message = "Guest channel opened for VM {VmId}.")]
    private static partial void LogOpened(ILogger logger, Guid vmId);

    [LoggerMessage(Level = LogLevel.Information, Message = "The guest channel of VM {VmId} ended while the VM is running; it will be opened again.")]
    private static partial void LogChannelEnded(ILogger logger, Guid vmId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A guest connection for VM {VmId} was dropped: its Hello did not match. Next attempt in {DelaySeconds} s.")]
    private static partial void LogBadHello(ILogger logger, Guid vmId, int delaySeconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Opening the guest channel for VM {VmId} failed ({ErrorType}). Next attempt in {DelaySeconds} s.")]
    private static partial void LogFailed(ILogger logger, Guid vmId, string errorType, int delaySeconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "VM {VmId} is listed as running but its open target is not usable (agent id, backend ref, OS or nonce). It is skipped.")]
    private static partial void LogInvalidTarget(ILogger logger, Guid vmId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reading the running VMs failed ({ErrorType}); asking again at the next poll.")]
    private static partial void LogListFailed(ILogger logger, string errorType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A guest channel opener poll failed ({ErrorType}).")]
    private static partial void LogPollFailed(ILogger logger, string errorType);

    [LoggerMessage(Level = LogLevel.Debug, Message = "VM {VmId} is no longer running; its open attempt and channel were released.")]
    private static partial void LogReleased(ILogger logger, Guid vmId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "The guest of VM {VmId} has not connected yet.")]
    private static partial void LogWaiting(ILogger logger, Guid vmId);

    /// <summary>What the opener keeps for one running VM. Mutated under the opener's gate.</summary>
    private sealed class VmWork(Guid vmId)
    {
        public Guid VmId { get; } = vmId;

        public Task? Attempt { get; set; }

        public CancellationTokenSource? Cts { get; set; }

        public GuestChannel? Channel { get; set; }

        public int Failures { get; set; }

        public DateTimeOffset NotBefore { get; set; }

        public DateTimeOffset LastEnded { get; set; }

        public bool WarnedInvalid { get; set; }

        public bool InFlight => Attempt is { IsCompleted: false };
    }
}
