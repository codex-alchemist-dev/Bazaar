using System.Runtime.CompilerServices;

namespace Bazaar.Server.Coding.Workspace;

/// <summary>
/// <see cref="IWorkspace"/> over one <see cref="IGuestChannel"/>, bound to a single worktree root in the guest.
/// </summary>
/// <remarks>
/// Each process runs in a background task. Its output is buffered in memory and numbered by position, so
/// <c>Seq</c> is the index in the buffer and the guest's own sequence numbers are not used. A stream replays from
/// <c>fromSeq</c> and then follows until the process ends. The timeout is enforced here as well as by the guest.
/// </remarks>
public sealed class GuestChannelWorkspace : IWorkspace
{
    private static readonly HashSet<string> _streams = new(StringComparer.Ordinal) { "stdout", "stderr" };

    private readonly IGuestChannel _channel;
    private readonly string _root;
    private readonly Dictionary<Guid, Run> _runs = [];
    private readonly object _gate = new();
    private bool _disposed;

    /// <summary>Creates a workspace over <paramref name="channel"/> rooted at <paramref name="worktreeRoot"/> in the guest.</summary>
    public GuestChannelWorkspace(IGuestChannel channel, string worktreeRoot)
    {
        ArgumentNullException.ThrowIfNull(channel);
        _channel = channel;
        _root = GuestPath.Resolve(worktreeRoot, ".");
    }

    /// <inheritdoc />
    public async Task<ProcessHandle> StartAsync(ProcessSpec spec, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ValidateSpec(spec);
        ct.ThrowIfCancellationRequested();

        var request = new ExecRequest(
            [.. spec.Argv],
            GuestPath.Resolve(_root, spec.Cwd),
            new Dictionary<string, string>(spec.Env),
            spec.Timeout);

        var run = new Run();
        var handle = new ProcessHandle(Guid.NewGuid());

        // Tying the process to ct: cancelling ct cancels the process, even after StartAsync has returned.
        run.Registration = ct.Register(static state => ((Run)state!).Cts.Cancel(), run);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _runs.Add(handle.Id, run);
            // The run's own source carries cancellation, so the Task.Run token is deliberately not forwarded.
            run.Task = Task.Run(() => ExecuteAsync(run, request), CancellationToken.None);
        }

        return handle;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<OutputChunk> StreamAsync(ProcessHandle h, long fromSeq, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fromSeq);
        var run = Lookup(h);

        var next = fromSeq;
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            OutputChunk? chunk = null;
            bool finished;
            Task changed;
            lock (run.Gate)
            {
                if (next < run.Chunks.Count)
                {
                    chunk = run.Chunks[(int)next];
                }

                finished = run.Finished;
                changed = run.Changed.Task;
            }

            if (chunk is not null)
            {
                next++;
                yield return chunk;
                continue;
            }

            if (finished)
            {
                yield break;
            }

            await changed.WaitAsync(ct);
        }
    }

    /// <inheritdoc />
    public async Task<ExecResult> RunAsync(ProcessSpec spec, CancellationToken ct)
    {
        var handle = await StartAsync(spec, ct);
        var run = Lookup(handle);
        await run.Task;
        return run.Result ?? throw new InvalidOperationException("A finished process has no result.");
    }

    /// <summary>Cancels running processes and waits for them to end. Output already buffered stays readable.</summary>
    public async ValueTask DisposeAsync()
    {
        List<Run> runs;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            runs = [.. _runs.Values];
        }

        foreach (var run in runs)
        {
            run.Cts.Cancel();
        }

        foreach (var run in runs)
        {
            try
            {
                await run.Task;
            }
            catch (Exception)
            {
                // A guest fault already faulted this run's task for RunAsync callers; disposal must not rethrow it.
            }
        }
    }

    private async Task ExecuteAsync(Run run, ExecRequest request)
    {
        // The guest runs out the clock too; this host-side timer is a backstop that reports TimedOut.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(run.Cts.Token);
        timeout.CancelAfter(request.Timeout);

        ExecResult? raw = null;
        try
        {
            raw = await _channel.ExecAsync(request, chunk => { Append(run, chunk); return Task.CompletedTask; }, timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested || run.Cts.IsCancellationRequested)
        {
            // Stopped by the timer or by a cancellation request; classified below.
        }
        finally
        {
            var cancelled = run.Cts.IsCancellationRequested;
            var timedOut = !cancelled && timeout.IsCancellationRequested;
            Finish(run, Classify(raw, timedOut, cancelled));
        }
    }

    private static ExecResult Classify(ExecResult? raw, bool timedOut, bool cancelled)
    {
        if (cancelled)
        {
            return new ExecResult(raw?.ExitCode ?? -1, TimedOut: false, Cancelled: true);
        }

        if (timedOut || raw?.TimedOut == true)
        {
            return new ExecResult(raw?.ExitCode ?? -1, TimedOut: true, Cancelled: false);
        }

        return raw ?? new ExecResult(-1, TimedOut: false, Cancelled: false);
    }

    private static void Append(Run run, OutputChunk chunk)
    {
        if (!_streams.Contains(chunk.Stream))
        {
            throw new InvalidDataException($"Unknown output stream '{chunk.Stream}'.");
        }

        lock (run.Gate)
        {
            run.Chunks.Add(chunk with { Seq = run.Chunks.Count });
            var previous = run.Changed;
            run.Changed = NewSignal();
            previous.TrySetResult();
        }
    }

    private static void Finish(Run run, ExecResult result)
    {
        run.Registration.Dispose();
        lock (run.Gate)
        {
            run.Result = result;
            run.Finished = true;
            run.Changed.TrySetResult();
        }
    }

    private Run Lookup(ProcessHandle h)
    {
        lock (_gate)
        {
            return _runs.TryGetValue(h.Id, out var run)
                ? run
                : throw new ArgumentException("Unknown process handle for this workspace.", nameof(h));
        }
    }

    private static void ValidateSpec(ProcessSpec spec)
    {
        if (spec.Argv.Count == 0 || spec.Argv.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("argv must be non-empty, with no blank elements.", nameof(spec));
        }

        ArgumentNullException.ThrowIfNull(spec.Env);
        if (spec.Timeout <= TimeSpan.Zero || spec.Timeout > TimeSpan.FromMilliseconds(int.MaxValue))
        {
            throw new ArgumentOutOfRangeException(nameof(spec), spec.Timeout, "Timeout must be positive and at most about 24 days.");
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Run
    {
        public object Gate { get; } = new();
        public CancellationTokenSource Cts { get; } = new();
        public List<OutputChunk> Chunks { get; } = [];
        public TaskCompletionSource Changed { get; set; } = NewSignal();
        public bool Finished { get; set; }
        public ExecResult? Result { get; set; }
        public Task Task { get; set; } = Task.CompletedTask;
        public CancellationTokenRegistration Registration { get; set; }
    }
}
