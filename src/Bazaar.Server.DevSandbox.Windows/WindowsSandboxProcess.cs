using System.Text;

namespace Bazaar.Server.DevSandbox.Windows;

/// <summary>
/// A process confined by a job object and an AppContainer. Owns the job, the AppContainer profile and the per-run work directory,
/// and removes all three on dispose.
/// </summary>
public sealed class WindowsSandboxProcess : ISandboxProcess
{
    private const int _maxCaptureChars = 256 * 1024;
    private static readonly TimeSpan _killWait = TimeSpan.FromSeconds(30);
    private readonly IWindowsConfinementApi _api;
    private readonly string? _profileName;
    private readonly StringBuilder _stdout = new();
    private readonly StringBuilder _stderr = new();
    private readonly Task _readers;
    private int _disposed;

    internal WindowsSandboxProcess(IWindowsConfinementApi api, IWindowsJob job, IWindowsProcess process, string? profileName, string workDirectory)
    {
        _api = api;
        Job = job;
        Process = process;
        _profileName = profileName;
        WorkDirectory = workDirectory;
        _readers = Task.WhenAll(
            Task.Run(() => Capture(process.StandardOutput, _stdout)),
            Task.Run(() => Capture(process.StandardError, _stderr)));
    }

    /// <summary>The per-run working directory. Only the current user, SYSTEM and the run's AppContainer can use it. It exists until the process is disposed.</summary>
    public string WorkDirectory { get; }

    /// <summary>The process id of the confined process.</summary>
    public int ProcessId => Process.Id;

    /// <summary>True once the process has exited.</summary>
    public bool HasExited => Process.HasExited;

    /// <summary>Exit code. Only valid after <see cref="HasExited"/>.</summary>
    public int ExitCode => Process.ExitCode;

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

    internal IWindowsJob Job { get; }

    internal IWindowsProcess Process { get; }

    /// <summary>Waits for the process to exit and its output to be drained.</summary>
    public async Task WaitForExitAsync(CancellationToken ct = default)
    {
        while (!await Process.WaitForExitAsync(TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false))
        {
        }

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
            Job.Terminate(1);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Reported by the wait below if the process survives; closing the job on dispose is the backstop.
        }

        try
        {
            // Belt and braces: the job should already have ended it.
            Process.Terminate(1);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Same: the wait below decides.
        }

        if (!await Process.WaitForExitAsync(_killWait, ct).ConfigureAwait(false))
        {
            throw new TimeoutException("The confined process did not exit after its job was terminated.");
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Every step runs even if an earlier one failed; closing the job handle kills anything left (kill-on-close).
        try
        {
            await KillCoreAsync(CancellationToken.None).ConfigureAwait(false);
            await _readers.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            // Best effort; the job close below is the backstop.
        }
        finally
        {
            WindowsCleanup.Run(_api, Job, Process, _profileName, WorkDirectory);
        }
    }

    private static void Capture(Stream stream, StringBuilder sink)
    {
        var decoder = Encoding.UTF8.GetDecoder();
        var bytes = new byte[4096];
        var chars = new char[4096];
        try
        {
            int n;
            while ((n = stream.Read(bytes, 0, bytes.Length)) > 0)
            {
                var c = decoder.GetChars(bytes, 0, n, chars, 0);
                lock (sink)
                {
                    if (sink.Length < _maxCaptureChars)
                    {
                        sink.Append(chars, 0, c);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The pipe was closed under us.
        }
    }
}
