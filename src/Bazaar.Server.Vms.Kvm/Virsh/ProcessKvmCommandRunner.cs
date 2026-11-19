using System.ComponentModel;
using System.Diagnostics;

namespace Bazaar.Server.Vms.Kvm.Virsh;

/// <summary>
/// Runs <c>virsh</c> and <c>qemu-img</c> with <see cref="Process"/>. Arguments go through <see cref="ProcessStartInfo.ArgumentList"/>, so nothing
/// is shell-parsed, and any other program is refused. A cancelled or timed-out call kills the whole process tree.
/// </summary>
public sealed class ProcessKvmCommandRunner(TimeSpan? timeout = null) : IKvmCommandRunner
{
    /// <summary>The default limit for one call. Graceful shutdown polls with separate short calls, so no single call runs long.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);

    private static readonly string[] _allowedPrograms = ["virsh", "qemu-img"];

    private readonly TimeSpan _timeout = timeout ?? DefaultTimeout;

    /// <inheritdoc />
    public async Task<KvmCommandResult> RunAsync(string program, IReadOnlyList<string> arguments, string? standardInput, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (Array.IndexOf(_allowedPrograms, program) < 0)
        {
            throw new ArgumentException($"Program '{program}' is not allowed; only virsh and qemu-img run.", nameof(program));
        }

        var startInfo = new ProcessStartInfo(program)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = StartProcess(program, startInfo);
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(_timeout);
        try
        {
            if (standardInput is not null)
            {
                await process.StandardInput.WriteAsync(standardInput.AsMemory(), limit.Token);
                process.StandardInput.Close();
            }

            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            KillTree(process);
            throw new KvmCommandException($"'{program}' did not finish within {_timeout}.");
        }
        catch (OperationCanceledException)
        {
            KillTree(process);
            throw;
        }

        return new KvmCommandResult(process.ExitCode, await stdout, await stderr);
    }

    private static Process StartProcess(string program, ProcessStartInfo startInfo)
    {
        try
        {
            return Process.Start(startInfo) ?? throw new KvmCommandException($"Could not start '{program}'.");
        }
        catch (Win32Exception ex)
        {
            throw new KvmCommandException($"Could not start '{program}': {ex.Message}", ex);
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
            // Already exited.
        }
    }
}
