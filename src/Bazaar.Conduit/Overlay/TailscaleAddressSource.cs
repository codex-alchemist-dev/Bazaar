using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Bazaar.Conduit;

/// <summary>
/// Reads the host's Tailscale IPv4 address with <c>tailscale ip -4</c>. The command runs without a shell and with a
/// fixed argument list. A non-zero exit, a timeout or unparseable output gives null. A start failure (for example the
/// CLI is missing) throws, so the module can report the exception type.
/// </summary>
public sealed class TailscaleAddressSource : IOverlayAddressSource
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(5);

    /// <inheritdoc />
    public async Task<IPAddress?> GetHostOverlayAddressAsync(CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("tailscale")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("ip");
        startInfo.ArgumentList.Add("-4");

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return null;
        }

        // Both streams are drained so a chatty CLI cannot block on a full pipe.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Kill(process);
            return null;
        }

        _ = await stderr.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            return null;
        }

        return ParseIPv4Output(await stdout.ConfigureAwait(false));
    }

    /// <summary>
    /// Parses the first non-empty line of <c>tailscale ip -4</c> output. Returns the address only when the line is an
    /// IPv4 address in canonical dotted form; anything else returns null.
    /// </summary>
    public static IPAddress? ParseIPv4Output(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var line = output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)[0];
        if (!IPAddress.TryParse(line, out var address)
            || address.AddressFamily != AddressFamily.InterNetwork
            || !string.Equals(address.ToString(), line, StringComparison.Ordinal))
        {
            return null;
        }

        return address;
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
        catch (InvalidOperationException)
        {
            // The process exited between the check and the kill.
        }
    }
}
