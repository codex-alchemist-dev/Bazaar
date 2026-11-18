using System.ComponentModel;
using System.Diagnostics;

namespace Bazaar.Server.Valkey;

/// <summary>
/// Starts the managed <c>valkey-server</c> as a hidden child process: no window, no shell, and output left on the host's streams.
/// Nothing is downloaded here; the binary must already exist at <see cref="ValkeyLaunchSpec.Executable"/>.
/// </summary>
public sealed class ValkeyProcessLauncher : IValkeyProcessLauncher
{
    /// <inheritdoc />
    public Task<IValkeyChild> StartAsync(ValkeyLaunchSpec spec, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ct.ThrowIfCancellationRequested();

        var start = new ProcessStartInfo(spec.Executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in spec.Arguments)
        {
            start.ArgumentList.Add(argument);
        }

        var process = Process.Start(start) ?? throw new InvalidOperationException("The valkey-server process did not start.");
        return Task.FromResult<IValkeyChild>(new ProcessValkeyChild(process));
    }

    /// <summary>Wraps one started process. Kill takes the whole tree, so no helper survives the server.</summary>
    private sealed class ProcessValkeyChild : IValkeyChild
    {
        private readonly Process _process;

        public ProcessValkeyChild(Process process)
        {
            _process = process;
            Exited = process.WaitForExitAsync();
        }

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
