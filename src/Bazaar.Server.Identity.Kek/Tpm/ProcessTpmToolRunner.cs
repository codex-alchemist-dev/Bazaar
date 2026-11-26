using System.ComponentModel;
using System.Diagnostics;

namespace Bazaar.Server.Identity.Kek.Tpm;

/// <summary>
/// Runs the binaries from <c>PATH</c> with <c>UseShellExecute</c> off and an argument list, so no argument is ever parsed by a shell.
/// The TPM is selected by the <c>TPM2TOOLS_TCTI</c> environment variable (for example <c>swtpm:host=127.0.0.1,port=2321</c> in tests).
/// Standard output is returned to the caller and never logged.
/// </summary>
public sealed class ProcessTpmToolRunner : ITpmToolRunner
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(60);

    public TpmToolResult Run(string tool, IReadOnlyList<string> arguments, byte[]? standardInput, string workingDirectory)
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
            startInfo.ArgumentList.Add(argument);

        using var process = StartOrThrow(startInfo);
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = new MemoryStream();
        var copy = process.StandardOutput.BaseStream.CopyToAsync(stdout);
        try
        {
            if (standardInput is not null)
                process.StandardInput.BaseStream.Write(standardInput);
        }
        catch (IOException)
        {
            // The tool exited before reading its input; its exit code and stderr say why.
        }

        process.StandardInput.Close();
        if (!process.WaitForExit(_timeout))
        {
            process.Kill(entireProcessTree: true);
            throw new KekCustodyException($"{tool} did not finish within {_timeout.TotalSeconds:0} seconds.");
        }

        copy.GetAwaiter().GetResult();
        var error = stderr.GetAwaiter().GetResult();
        return new TpmToolResult(process.ExitCode, stdout.ToArray(), error.Length > 500 ? error[..500] : error);
    }

    private static Process StartOrThrow(ProcessStartInfo startInfo)
    {
        try
        {
            return Process.Start(startInfo) ?? throw new KekCustodyException($"{startInfo.FileName} could not be started.");
        }
        catch (Win32Exception ex)
        {
            throw new KekCustodyException($"{startInfo.FileName} is not installed. Install it to use identity.kek_provider 'tpm' (doc 14).", ex);
        }
    }
}
