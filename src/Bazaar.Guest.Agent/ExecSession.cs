using System.ComponentModel;
using System.Diagnostics;
using System.Threading.Channels;
using Bazaar.Contracts.Vms;

namespace Bazaar.Guest.Agent;

/// <summary>
/// One running exec: the process, its stdout and stderr pumps, and its timeout. <see cref="TryStart"/> starts the
/// process. <see cref="Completion"/> finishes once every output chunk and the Exit frame are queued. The exit reason is
/// whichever comes first, the timeout or a cancel, and it is decided only once.
/// </summary>
internal sealed class ExecSession : IDisposable
{
    private const int _reasonNone = 0;
    private const int _reasonTimedOut = 1;
    private const int _reasonCancelled = 2;

    private readonly Exec _exec;
    private readonly OutboundQueue _outbound;
    private CancellationTokenSource? _timeout;
    private Process? _process;
    private bool _ownsGroup;
    private int _reason;

    /// <summary>Creates a session. Nothing runs until <see cref="TryStart"/>.</summary>
    public ExecSession(Exec exec, OutboundQueue outbound)
    {
        _exec = exec;
        _outbound = outbound;
    }

    /// <summary>Finishes after the Exit frame is queued. Assigned by <see cref="TryStart"/>.</summary>
    public Task Completion { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Starts the process and arms the timeout. The environment is only the variables named in the allowlist,
    /// read from the agent's own environment. Returns false with an error when the program cannot start.
    /// </summary>
    public bool TryStart(out string? error)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in _exec.EnvAllowlist)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (value is not null)
            {
                environment[name] = value;
            }
        }

        Process process;
        bool ownsGroup;
        try
        {
            (process, ownsGroup) = ChildProcess.Start(_exec.Argv, _exec.Cwd, environment);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or ArgumentException or NotSupportedException)
        {
            error = $"could not start '{_exec.Argv[0]}': {ex.Message}";
            return false;
        }

        // The exec gets end of file on stdin, so a program that reads input cannot hang the exec.
        process.StandardInput.Close();
        _process = process;
        _ownsGroup = ownsGroup;
        _timeout = new CancellationTokenSource(TimeSpan.FromSeconds(_exec.TimeoutSec));
        _ = _timeout.Token.Register(() => Kill(_reasonTimedOut));
        Completion = ReportAsync(process);
        error = null;
        return true;
    }

    /// <summary>Kills the exec's process tree and reports the exit as cancelled. Has no effect after the exit reason is set.</summary>
    public void Cancel() => Kill(_reasonCancelled);

    /// <inheritdoc />
    public void Dispose()
    {
        _timeout?.Dispose();
        _process?.Dispose();
    }

    private async Task ReportAsync(Process process)
    {
        try
        {
            var stdout = PumpAsync(process.StandardOutput.BaseStream, OutputSource.Stdout);
            var stderr = PumpAsync(process.StandardError.BaseStream, OutputSource.Stderr);
            await process.WaitForExitAsync();

            // Exit is sent only after both pipes reach end of file. A background child that keeps a pipe open holds the
            // exec until the timeout kills the tree, and that is reported as a timeout.
            await Task.WhenAll(stdout, stderr);

            var reason = Volatile.Read(ref _reason);
            var code = process.ExitCode;
            if (reason != _reasonNone && code == 0)
            {
                code = -1;
            }

            await _outbound.SendAsync(new Exit(_exec.Id, code, reason == _reasonTimedOut), CancellationToken.None);
        }
        catch (ChannelClosedException)
        {
            // The host is gone, so there is nobody left to report to.
        }
    }

    private async Task PumpAsync(Stream source, OutputSource stream)
    {
        var buffer = new byte[OutboundQueue.MaxPayloadBytes];
        long seq = 0;
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer);
                if (read == 0)
                {
                    return;
                }

                var chunk = buffer.AsSpan(0, read).ToArray();
                await _outbound.SendAsync(new Output(_exec.Id, stream, seq++, chunk), CancellationToken.None);
            }
        }
        catch (IOException)
        {
            // The kill closed the pipe under the read. The exit path still reports.
        }
    }

    private void Kill(int reason)
    {
        if (Interlocked.CompareExchange(ref _reason, reason, _reasonNone) != _reasonNone)
        {
            return;
        }

        if (_process is { } process)
        {
            ChildProcess.KillTree(process, _ownsGroup);
        }
    }
}
