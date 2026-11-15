using System.ComponentModel;
using System.Diagnostics;
using Bazaar.Secrets.Keyring;

namespace Bazaar.Secrets.Tpm;

/// <summary>
/// Runs the tpm2-tools binaries from <c>PATH</c>. The TPM is selected by the <c>TPM2TOOLS_TCTI</c> environment variable,
/// for example <c>swtpm:host=127.0.0.1,port=2321</c> in tests or the device TCTI on a real host.
/// </summary>
public sealed class ProcessTpmToolRunner : ITpmToolRunner
{
    public async Task<TpmToolResult> RunAsync(string tool, IReadOnlyList<string> arguments, byte[]? standardInput, string workingDirectory, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tool);
        ArgumentNullException.ThrowIfNull(arguments);
        var startInfo = new ProcessStartInfo(tool)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDirectory,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = StartOrThrow(startInfo);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        var stdout = new MemoryStream();
        var copy = process.StandardOutput.BaseStream.CopyToAsync(stdout, ct);
        if (standardInput is not null)
        {
            await process.StandardInput.BaseStream.WriteAsync(standardInput, ct).ConfigureAwait(false);
        }

        process.StandardInput.Close();
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        await copy.ConfigureAwait(false);
        return new TpmToolResult(process.ExitCode, stdout.ToArray(), await stderr.ConfigureAwait(false));
    }

    private static Process StartOrThrow(ProcessStartInfo startInfo)
    {
        try
        {
            return Process.Start(startInfo) ?? throw new KekCustodyException($"{startInfo.FileName} could not be started.");
        }
        catch (Win32Exception ex)
        {
            throw new KekCustodyException($"{startInfo.FileName} is not installed. Install tpm2-tools to use the TPM KEK.", ex);
        }
    }
}
