using System.ComponentModel;
using System.Diagnostics;

namespace Bazaar.Secrets.Keyring;

/// <summary>
/// Runs the real <c>secret-tool</c> binary. Needs a running Secret Service (for example GNOME Keyring) on the session bus. The binary
/// is never looked up through <c>PATH</c>: it is the configured absolute path, else the first of the standard system locations that
/// exists. Every call is bounded by a timeout (a locked keyring can wait on an unlock prompt for ever); on timeout the process is killed
/// and <see cref="KekCustodyException"/> is thrown.
/// </summary>
public sealed class ProcessSecretToolRunner : ISecretToolRunner
{
    /// <summary>The per-call timeout when none is configured.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private static readonly string[] _standardLocations =
    [
        "/usr/bin/secret-tool",
        "/usr/local/bin/secret-tool",
        "/bin/secret-tool",
        "/run/current-system/sw/bin/secret-tool",
    ];

    private readonly string? _executablePath;
    private readonly TimeSpan _timeout;

    /// <summary>Creates a runner. <paramref name="executablePath"/> must be absolute when given.</summary>
    public ProcessSecretToolRunner(string? executablePath = null, TimeSpan? timeout = null)
    {
        if (!string.IsNullOrWhiteSpace(executablePath) && !Path.IsPathFullyQualified(executablePath))
        {
            throw new ArgumentException("The secret-tool path must be absolute.", nameof(executablePath));
        }

        _executablePath = string.IsNullOrWhiteSpace(executablePath) ? null : executablePath;
        _timeout = timeout ?? DefaultTimeout;
        if (_timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "The secret-tool timeout must be positive.");
        }
    }

    public async Task<SecretToolResult> RunAsync(IReadOnlyList<string> arguments, string? standardInput, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var startInfo = new ProcessStartInfo(ResolveExecutable())
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

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutSource.CancelAfter(_timeout);
        using var process = StartOrThrow(startInfo);
        try
        {
            var stderr = process.StandardError.ReadToEndAsync(timeoutSource.Token);
            var stdout = process.StandardOutput.ReadToEndAsync(timeoutSource.Token);
            if (standardInput is not null)
            {
                await process.StandardInput.WriteAsync(standardInput.AsMemory(), timeoutSource.Token).ConfigureAwait(false);
            }

            process.StandardInput.Close();
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            return new SecretToolResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new KekCustodyException($"secret-tool did not answer within {_timeout.TotalSeconds:0.#} s (the keyring may be locked and waiting for an unlock).");
        }
        finally
        {
            Kill(process);
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // The process ended between the check and the kill.
        }
    }

    private string ResolveExecutable()
    {
        if (_executablePath is not null)
        {
            return _executablePath;
        }

        foreach (var candidate in _standardLocations)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new KekCustodyException("secret-tool is not installed in a standard location. Install libsecret-tools and a Secret Service keyring (for example GNOME Keyring), or set its absolute path.");
    }

    private static Process StartOrThrow(ProcessStartInfo startInfo)
    {
        try
        {
            return Process.Start(startInfo) ?? throw new KekCustodyException("secret-tool could not be started.");
        }
        catch (Win32Exception ex)
        {
            throw new KekCustodyException("secret-tool could not be started. Check its path and permissions.", ex);
        }
    }
}
