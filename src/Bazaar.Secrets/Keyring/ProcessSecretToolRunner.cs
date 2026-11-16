using System.ComponentModel;
using System.Diagnostics;

namespace Bazaar.Secrets.Keyring;

/// <summary>Runs the real <c>secret-tool</c> binary from <c>PATH</c>. Needs a running Secret Service (for example GNOME Keyring) on the session bus.</summary>
public sealed class ProcessSecretToolRunner : ISecretToolRunner
{
    private const string _executable = "secret-tool";

    public async Task<SecretToolResult> RunAsync(IReadOnlyList<string> arguments, string? standardInput, CancellationToken ct)
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

        using var process = StartOrThrow(startInfo);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        if (standardInput is not null)
        {
            await process.StandardInput.WriteAsync(standardInput.AsMemory(), ct).ConfigureAwait(false);
        }

        process.StandardInput.Close();
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        return new SecretToolResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    private static Process StartOrThrow(ProcessStartInfo startInfo)
    {
        try
        {
            return Process.Start(startInfo) ?? throw new KekCustodyException("secret-tool could not be started.");
        }
        catch (Win32Exception ex)
        {
            throw new KekCustodyException("secret-tool is not installed. Install libsecret-tools and a Secret Service keyring (for example GNOME Keyring).", ex);
        }
    }
}
