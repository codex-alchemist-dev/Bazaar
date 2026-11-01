using System.ComponentModel;
using System.Diagnostics;

namespace Bazaar.Components.ManagedPostgres;

/// <summary>Runs PostgreSQL tools with System.Diagnostics.Process. Arguments go through ArgumentList, so nothing is shell-parsed.</summary>
public sealed class ProcessPostgresCommandRunner : IPostgresCommandRunner
{
    private static readonly TimeSpan AttachedPollInterval = TimeSpan.FromMilliseconds(250);

    /// <inheritdoc />
    public async Task<PostgresCommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = CreateStartInfo(fileName, arguments);
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;

        using var process = StartProcess(fileName, startInfo);
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            KillTree(process);
            throw;
        }

        var output = await stdout + await stderr;
        return new PostgresCommandResult(process.ExitCode, output);
    }

    /// <inheritdoc />
    public IPostgresChild Start(string fileName, IReadOnlyList<string> arguments)
    {
        var process = StartProcess(fileName, CreateStartInfo(fileName, arguments));
        return new ProcessChild(process);
    }

    /// <inheritdoc />
    public IPostgresChild Attach(int processId) => new AttachedChild(processId);

    /// <inheritdoc />
    public bool IsProcessAlive(int processId) => IsAlive(processId);

    private static ProcessStartInfo CreateStartInfo(string fileName, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static Process StartProcess(string fileName, ProcessStartInfo startInfo)
    {
        try
        {
            return Process.Start(startInfo) ?? throw new PostgresProcessException($"Could not start '{fileName}'.");
        }
        catch (Win32Exception ex)
        {
            throw new PostgresProcessException($"Could not start '{fileName}': {ex.Message}", ex);
        }
    }

    private static void KillTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            return;
        }
    }

    private static bool IsAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (Win32Exception)
        {
            return true;
        }
    }

    private sealed class ProcessChild(Process process) : IPostgresChild
    {
        public int ProcessId => process.Id;

        public bool HasExited => process.HasExited;

        public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
        {
            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode;
        }

        public void Kill()
        {
            try
            {
                process.Kill(entireProcessTree: false);
            }
            catch (InvalidOperationException)
            {
                return;
            }
        }

        public void Dispose() => process.Dispose();
    }

    private sealed class AttachedChild(int processId) : IPostgresChild
    {
        public int ProcessId { get; } = processId;

        public bool HasExited => !IsAlive(ProcessId);

        public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
        {
            while (IsAlive(ProcessId))
            {
                await Task.Delay(AttachedPollInterval, cancellationToken);
            }

            return -1;
        }

        public void Kill()
        {
            try
            {
                Process.GetProcessById(ProcessId).Kill(entireProcessTree: false);
            }
            catch (ArgumentException)
            {
                return;
            }
            catch (InvalidOperationException)
            {
                return;
            }
        }

        public void Dispose()
        {
        }
    }
}
