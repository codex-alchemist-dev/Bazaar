using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Bazaar.Server.BrowserPool.LinuxHost;

/// <summary>One process to run: an argument vector (never a shell string), optional stdin text, and the limits.</summary>
/// <param name="Argv">Program and arguments; element 0 is the program.</param>
/// <param name="StdIn">Text written to the process's stdin, which is then closed. Null closes stdin at once.</param>
/// <param name="Timeout">How long a run-to-completion may take. Ignored by <see cref="IProcessRunner.Start"/>.</param>
/// <param name="MaxCapturedChars">The most stdout and the most stderr that are kept. The rest is read and discarded.</param>
internal sealed record ProcessRequest(IReadOnlyList<string> Argv, string? StdIn, TimeSpan Timeout, int MaxCapturedChars);

/// <summary>The outcome of a run to completion.</summary>
internal sealed record ProcessResult(int ExitCode, string StdOut, string StdErr, bool StdErrTruncated);

/// <summary>A started, still-running process.</summary>
internal interface IRunningProcess : IAsyncDisposable
{
    /// <summary>The OS process id.</summary>
    int Id { get; }

    /// <summary>Completes with the exit code when the process (not its children) ends.</summary>
    Task<int> Exited { get; }

    /// <summary>The bounded capture of stderr; complete only once <see cref="Exited"/> has completed.</summary>
    string StdErr { get; }

    /// <summary>Kills the process and everything below it. Safe to call after exit.</summary>
    void KillTree();
}

/// <summary>The only place the adapter creates processes. A fake stands in for it in tests.</summary>
internal interface IProcessRunner
{
    /// <summary>Runs to completion. Cancellation and timeout kill the process tree.</summary>
    /// <exception cref="TimeoutException">The run exceeded <see cref="ProcessRequest.Timeout"/>.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken ct);

    /// <summary>Starts a long-running process and returns once it has been spawned.</summary>
    IRunningProcess Start(ProcessRequest request);
}

/// <summary><see cref="IProcessRunner"/> over <see cref="Process"/>: no shell, one <c>ArgumentList</c> entry per argument.</summary>
internal sealed class ProcessRunner : IProcessRunner
{
    private static readonly TimeSpan _drainGrace = TimeSpan.FromSeconds(5);

    public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var process = Create(request);
        Spawn(process, request);
        var outTask = ReadBoundedAsync(process.StandardOutput, request.MaxCapturedChars);
        var errTask = ReadBoundedAsync(process.StandardError, request.MaxCapturedChars);

        using var timeoutCts = new CancellationTokenSource(request.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            await WriteStdInAsync(process, request.StdIn, linked.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillTree(process);
            await WaitQuietlyAsync(process).ConfigureAwait(false);
            if (ct.IsCancellationRequested)
            {
                throw;
            }

            throw new TimeoutException("A pool host command exceeded its time limit and was killed.");
        }

        // A grandchild that kept a pipe open must not hold the call up for ever.
        await DrainAsync(outTask, errTask).ConfigureAwait(false);
        var (stdout, _) = outTask.IsCompletedSuccessfully ? outTask.Result : (string.Empty, true);
        var (stderr, truncated) = errTask.IsCompletedSuccessfully ? errTask.Result : (string.Empty, true);
        return new ProcessResult(process.ExitCode, stdout, stderr, truncated);
    }

    public IRunningProcess Start(ProcessRequest request)
    {
        var process = Create(request);
        try
        {
            Spawn(process, request);
            // Nothing is ever written to a browser's stdin; closing it makes a stray read see end of file.
            process.StandardInput.Close();
            return new RunningProcess(process, request.MaxCapturedChars);
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    internal static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
        catch (Win32Exception)
        {
            // Raced with exit; the caller waits for the exit anyway.
        }
    }

    internal static async Task<(string Text, bool Truncated)> ReadBoundedAsync(StreamReader reader, int max)
    {
        var sb = new StringBuilder();
        var buffer = new char[4096];
        var truncated = false;
        int n;
        while ((n = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
        {
            var room = max - sb.Length;
            if (room > 0)
            {
                var take = Math.Min(room, n);
                sb.Append(buffer, 0, take);
                truncated |= take < n;
            }
            else
            {
                truncated = true;
            }
        }

        return (sb.ToString(), truncated);
    }

    private static async Task DrainAsync(Task first, Task second)
    {
        try
        {
            await Task.WhenAll(first, second).WaitAsync(_drainGrace).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // A grandchild still holds the pipe; what was read so far is all there will be.
        }
    }

    private static Process Create(ProcessRequest request)
    {
        if (request.Argv.Count == 0 || string.IsNullOrEmpty(request.Argv[0]))
        {
            throw new ArgumentException("A command needs a program.", nameof(request));
        }

        var info = new ProcessStartInfo(request.Argv[0])
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            CreateNoWindow = true,
        };
        for (var i = 1; i < request.Argv.Count; i++)
        {
            info.ArgumentList.Add(request.Argv[i]);
        }

        return new Process { StartInfo = info };
    }

    private static void Spawn(Process process, ProcessRequest request)
    {
        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            // Name the program only: later arguments can carry proxy addresses.
            throw new InvalidOperationException("Could not start " + request.Argv[0] + ": " + ex.Message, ex);
        }
    }

    private static async Task WriteStdInAsync(Process process, string? text, CancellationToken ct)
    {
        try
        {
            if (!string.IsNullOrEmpty(text))
            {
                await process.StandardInput.WriteAsync(text.AsMemory(), ct).ConfigureAwait(false);
                await process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
            }
        }
        catch (IOException)
        {
            // The program exited without reading everything; its exit code and stderr say why.
        }
        finally
        {
            try
            {
                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // Same.
            }
        }
    }

    private static async Task WaitQuietlyAsync(Process process)
    {
        using var cts = new CancellationTokenSource(_drainGrace);
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Killed but not reaped within the grace period: nothing more to do from here.
        }
    }

    private sealed class RunningProcess : IRunningProcess
    {
        private readonly Process _process;
        private readonly Task<(string Text, bool Truncated)> _err;
        private readonly Task<(string Text, bool Truncated)> _out;

        public RunningProcess(Process process, int maxChars)
        {
            _process = process;
            Id = process.Id;
            _out = ReadBoundedAsync(process.StandardOutput, maxChars);
            _err = ReadBoundedAsync(process.StandardError, maxChars);
            Exited = WaitAsync();
        }

        public int Id { get; }

        public Task<int> Exited { get; }

        public string StdErr => _err.IsCompletedSuccessfully ? _err.Result.Text : string.Empty;

        public void KillTree() => ProcessRunner.KillTree(_process);

        public async ValueTask DisposeAsync()
        {
            KillTree();
            await Exited.ConfigureAwait(false);
            _process.Dispose();
        }

        private async Task<int> WaitAsync()
        {
            await _process.WaitForExitAsync().ConfigureAwait(false);
            await DrainAsync(_out, _err).ConfigureAwait(false);
            return _process.ExitCode;
        }
    }
}
