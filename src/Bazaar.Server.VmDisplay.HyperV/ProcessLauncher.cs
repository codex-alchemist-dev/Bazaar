using System.Diagnostics;
using System.Text;
using Bazaar.Server.VmDisplay.Abstractions;

namespace Bazaar.Server.VmDisplay.HyperV;

/// <summary>One process to run: argv, environment additions and the text written to its standard input.</summary>
internal sealed record ProcessSpec(
    string FileName,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment,
    string StdIn,
    TimeSpan Timeout,
    int MaxStdoutChars);

/// <summary>The outcome of one process run.</summary>
internal sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);

/// <summary>Runs one executable. No shell is involved.</summary>
internal interface IProcessLauncher
{
    Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct);
}

/// <summary>Starts processes through <see cref="Process"/> with <see cref="ProcessStartInfo.ArgumentList"/>.</summary>
internal sealed class SystemProcessLauncher : IProcessLauncher
{
    private const int MaxStderrChars = 2048;

    public async Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(spec.FileName)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string a in spec.Arguments)
            psi.ArgumentList.Add(a);
        foreach ((string key, string value) in spec.Environment)
            psi.Environment[key] = value;

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new DisplayException(DisplayFailure.SourceFailed, "The PowerShell executable could not be started.", e);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(spec.Timeout);
        try
        {
            Task<string> stdout = ReadCappedAsync(process.StandardOutput, spec.MaxStdoutChars, cts.Token);
            Task<string> stderr = ReadCappedAsync(process.StandardError, MaxStderrChars, cts.Token);
            await process.StandardInput.WriteAsync(spec.StdIn.AsMemory(), cts.Token).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return new ProcessResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Kill(process);
            throw new DisplayException(DisplayFailure.SourceFailed, "The PowerShell call timed out.");
        }
        catch (DisplayException)
        {
            Kill(process);
            throw;
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }
    }

    private static async Task<string> ReadCappedAsync(StreamReader reader, int maxChars, CancellationToken ct)
    {
        var sb = new StringBuilder();
        char[] buffer = new char[8192];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            sb.Append(buffer, 0, read);
            if (sb.Length > maxChars)
                throw new DisplayException(DisplayFailure.FrameTooLarge, "The PowerShell output is larger than the limit.");
        }

        return sb.ToString();
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // The process already ended.
        }
    }
}
