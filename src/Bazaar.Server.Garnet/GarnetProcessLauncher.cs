using System.ComponentModel;
using System.Diagnostics;
using Bazaar.Server.Valkey;

namespace Bazaar.Server.Garnet;

/// <summary>
/// Starts the managed Garnet as a hidden child process with no window and no shell. Nothing is downloaded: the binary must already
/// exist. Windows only for now: on any other OS the launcher refuses, so the cache never runs unprotected.
/// <para>
/// Parent death: every child is assigned to a Windows Job object with kill-on-close (<see cref="WindowsJobObject"/>). If the assignment
/// fails the child is killed and the start fails (fail closed).
/// </para>
/// </summary>
public sealed class GarnetProcessLauncher : IGarnetProcessLauncher, IDisposable
{
    private readonly Lock _gate = new();
    private WindowsJobObject? _job;

    /// <inheritdoc />
    public Task<IValkeyChild> StartAsync(GarnetLaunchSpec spec, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ct.ThrowIfCancellationRequested();

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The managed Garnet server is supported on Windows only.");
        }

        return Task.FromResult<IValkeyChild>(Start(spec));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (OperatingSystem.IsWindows())
            {
                _job?.Dispose();
            }

            _job = null;
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private ProcessGarnetChild Start(GarnetLaunchSpec spec)
    {
        var start = new ProcessStartInfo(spec.Executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in spec.Arguments)
        {
            start.ArgumentList.Add(argument);
        }

        var job = Job();
        var process = Process.Start(start) ?? throw new InvalidOperationException("The Garnet process did not start.");
        try
        {
            job.Assign(process.SafeHandle.DangerousGetHandle());
        }
        catch (Exception)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                // Already gone.
            }

            process.Dispose();
            throw;
        }

        return new ProcessGarnetChild(process);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private WindowsJobObject Job()
    {
        lock (_gate)
        {
            return _job ??= new WindowsJobObject();
        }
    }

    /// <summary>Wraps one started process. Kill takes the whole tree, so no helper survives the server.</summary>
    private sealed class ProcessGarnetChild : IValkeyChild
    {
        private readonly Process _process;

        public ProcessGarnetChild(Process process)
        {
            _process = process;
            Exited = process.WaitForExitAsync();
        }

        /// <inheritdoc />
        public int ProcessId => _process.Id;

        /// <inheritdoc />
        public Task Exited { get; }

        /// <inheritdoc />
        public void Kill()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                // The process exited between the check and the kill; nothing is left to stop.
            }
        }

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            Kill();
            _process.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
