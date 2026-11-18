using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;

namespace Bazaar.Server.Valkey;

/// <summary>
/// Starts the managed <c>valkey-server</c> as a hidden child process with no window and no shell. The configuration is written to the
/// child's stdin, so no password reaches the command line or a file. Nothing is downloaded: the binary must already exist.
/// <para>
/// Parent death: on Linux the server runs under the parent-death wrapper (<see cref="ValkeyLaunchSpec.ParentDeathWrapper"/>), which sets
/// <c>PR_SET_PDEATHSIG</c> to SIGKILL and then execs the server. The kernel sends the signal when the thread that forked the child exits,
/// so every start runs on one dedicated background thread that lives as long as the host. Without the wrapper the start is refused:
/// the cache does not run unprotected.
/// </para>
/// </summary>
public sealed class ValkeyProcessLauncher : IValkeyProcessLauncher
{
    /// <summary>Signal the wrapper sets for the child when its parent dies.</summary>
    internal const string ParentDeathSignal = "SIGKILL";

    private readonly BlockingCollection<Action> _work = [];
    private readonly Lock _gate = new();
    private Thread? _thread;

    /// <inheritdoc />
    public Task<IValkeyChild> StartAsync(ValkeyLaunchSpec spec, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ct.ThrowIfCancellationRequested();

        var completion = new TaskCompletionSource<IValkeyChild>(TaskCreationOptions.RunContinuationsAsynchronously);
        EnsureLaunchThread();
        _work.Add(() =>
        {
            try
            {
                completion.TrySetResult(Start(spec));
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        }, ct);

        return completion.Task.WaitAsync(ct);
    }

    /// <summary>The program and arguments for a spec. Pure, so the wrapper rule is testable without a process.</summary>
    internal static (string FileName, IReadOnlyList<string> Arguments) Command(ValkeyLaunchSpec spec)
    {
        if (spec.ParentDeathWrapper.Length == 0)
        {
            return (spec.Executable, spec.Arguments);
        }

        List<string> arguments = ["--pdeathsig", ParentDeathSignal, "--", spec.Executable];
        arguments.AddRange(spec.Arguments);
        return (spec.ParentDeathWrapper, arguments);
    }

    private static ProcessValkeyChild Start(ValkeyLaunchSpec spec)
    {
        if (spec.ParentDeathWrapper.Length == 0 && OperatingSystem.IsLinux())
        {
            throw new InvalidOperationException("A managed Valkey on Linux needs a parent-death wrapper.");
        }

        var (fileName, arguments) = Command(spec);
        var start = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        var process = Process.Start(start) ?? throw new InvalidOperationException("The valkey-server process did not start.");
        try
        {
            process.StandardInput.Write(spec.ConfigText);
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // The child exited before reading its configuration. Its exit is reported through Exited.
        }

        return new ProcessValkeyChild(process);
    }

    private void EnsureLaunchThread()
    {
        lock (_gate)
        {
            if (_thread is not null)
            {
                return;
            }

            _thread = new Thread(() =>
            {
                foreach (var action in _work.GetConsumingEnumerable())
                {
                    action();
                }
            })
            {
                IsBackground = true,
                Name = "bazaar-valkey-launch",
            };
            _thread.Start();
        }
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
