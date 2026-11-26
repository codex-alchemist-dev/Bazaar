using System.ComponentModel;
using System.Diagnostics;

namespace Bazaar.Client.Updater.Linux;

/// <summary>Runs a real child process with redirected output and no shell.</summary>
public sealed class SystemProcessRunner : IProcessRunner
{
    /// <inheritdoc />
    public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var info = new ProcessStartInfo(request.FileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in request.Arguments)
        {
            info.ArgumentList.Add(argument);
        }

        foreach (var pair in request.Environment)
        {
            info.Environment[pair.Key] = pair.Value;
        }

        using var process = new Process { StartInfo = info };
        try
        {
            process.Start();
        }
        catch (Win32Exception e)
        {
            throw new IOException($"Could not start '{request.FileName}': {e.Message}", e);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(request.Timeout);
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return new ProcessResult(
                process.ExitCode,
                await stdout.ConfigureAwait(false),
                await stderr.ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"'{request.FileName}' did not finish within {request.Timeout}.");
        }
    }
}
