using System.ComponentModel;
using System.Diagnostics;

namespace Bazaar.Client.Linux.Secrets;

/// <summary>Runs the real <c>secret-tool</c> binary from <c>PATH</c>.</summary>
public sealed class ProcessSecretToolRunner : ISecretToolRunner
{
    private const string _executable = "secret-tool";

    public async Task<SecretToolResult> RunAsync(IReadOnlyList<string> arguments, string? standardInput, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var startInfo = new ProcessStartInfo(_executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = StartOrThrow(startInfo);
        // Stderr is drained concurrently so a chatty tool cannot block on a full pipe.
        Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);

        if (standardInput is not null)
        {
            await process.StandardInput.WriteAsync(standardInput.AsMemory(), cancellationToken).ConfigureAwait(false);
        }

        process.StandardInput.Close();
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        await stderr.ConfigureAwait(false);
        return new SecretToolResult(process.ExitCode, await stdout.ConfigureAwait(false));
    }

    private static Process StartOrThrow(ProcessStartInfo startInfo)
    {
        try
        {
            return Process.Start(startInfo)
                ?? throw new SecretStoreUnavailableException("secret-tool could not be started.");
        }
        catch (Win32Exception ex)
        {
            throw new SecretStoreUnavailableException(
                "secret-tool is not installed. Install libsecret-tools and a Secret Service keyring (for example GNOME Keyring).", ex);
        }
    }
}
