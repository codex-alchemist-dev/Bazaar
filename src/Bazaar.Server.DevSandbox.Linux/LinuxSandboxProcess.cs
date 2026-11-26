using System.Diagnostics;
using System.Text;

namespace Bazaar.Server.DevSandbox.Linux;

/// <summary>A process confined by namespaces and rlimits. Owns the per-run working directory and removes it on dispose.</summary>
public sealed class LinuxSandboxProcess : ISandboxProcess
{
    private const int _maxCaptureChars = 256 * 1024;
    private readonly Process _process;
    private readonly StringBuilder _stdout = new();
    private readonly StringBuilder _stderr = new();
    private readonly Task _readers;
    private int _disposed;

    internal LinuxSandboxProcess(Process process, string workDirectory)
    {
        _process = process;
        WorkDirectory = workDirectory;
        _readers = Task.WhenAll(Capture(process.StandardOutput, _stdout), Capture(process.StandardError, _stderr));
    }

    /// <summary>The empty, private (mode 0700) working directory. It exists until the process is disposed.</summary>
    public string WorkDirectory { get; }

    /// <summary>The host process id of the outermost wrapper (inside the pid namespace the module is pid 1).</summary>
    public int HostProcessId => _process.Id;

    /// <summary>True once the process has exited.</summary>
    public bool HasExited => _process.HasExited;

    /// <summary>Exit code. Only valid after <see cref="HasExited"/>.</summary>
    public int ExitCode => _process.ExitCode;

    /// <summary>Captured standard output so far (capped).</summary>
    public string Stdout
    {
        get
        {
            lock (_stdout)
            {
                return _stdout.ToString();
            }
        }
    }

    /// <summary>Captured standard error so far (capped).</summary>
    public string Stderr
    {
        get
        {
            lock (_stderr)
            {
                return _stderr.ToString();
            }
        }
    }

    /// <summary>Waits for the process to exit and its output to be drained.</summary>
    public async Task WaitForExitAsync(CancellationToken ct = default)
    {
        await _process.WaitForExitAsync(ct).ConfigureAwait(false);
        await _readers.WaitAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task KillAsync(CancellationToken ct = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        await KillCoreAsync(ct).ConfigureAwait(false);
    }

    private async Task KillCoreAsync(CancellationToken ct)
    {
        try
        {
            if (!_process.HasExited)
            {
                // Killing the wrapper also ends the pid namespace's init (--kill-child), which kills everything inside it;
                // the tree kill covers any child visible from the host.
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }

        await _process.WaitForExitAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await KillCoreAsync(CancellationToken.None).ConfigureAwait(false);
            await _readers.ConfigureAwait(false);
        }
        finally
        {
            _process.Dispose();
            try
            {
                Directory.Delete(WorkDirectory, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }

    private static async Task Capture(StreamReader reader, StringBuilder sink)
    {
        var buffer = new char[4096];
        int n;
        while ((n = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            lock (sink)
            {
                if (sink.Length < _maxCaptureChars)
                {
                    sink.Append(buffer, 0, n);
                }
            }
        }
    }
}
