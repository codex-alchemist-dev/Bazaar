using System.Diagnostics;
using System.Text;

namespace Bazaar.Server.Verification;

public sealed record ProcessResult(int ExitCode, bool TimedOut, string Output);

/// <summary>Seam for running external commands; the gate never trusts an exit code it did not observe itself.</summary>
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout, CancellationToken ct = default);
}

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout, CancellationToken ct = default)
    {
        var info = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in arguments)
        {
            info.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = info };
        var output = new StringBuilder();
        var sync = new object();
        void Append(string? line)
        {
            if (line is null)
            {
                return;
            }

            lock (sync)
            {
                output.AppendLine(line);
            }
        }

        process.OutputDataReceived += (_, e) => Append(e.Data);
        process.ErrorDataReceived += (_, e) => Append(e.Data);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
            process.WaitForExit(); // flush async output handlers
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            lock (sync)
            {
                return new ProcessResult(-1, true, output.ToString());
            }
        }

        lock (sync)
        {
            return new ProcessResult(process.ExitCode, false, output.ToString());
        }
    }
}
