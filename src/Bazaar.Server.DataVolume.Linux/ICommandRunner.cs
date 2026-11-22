using System.Diagnostics;
using System.Text;

namespace Bazaar.Server.DataVolume.Linux;

/// <summary>The outcome of one command. A non-zero exit code is a result, not an exception.</summary>
internal sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>
/// Runs an external program with an argv list (never a shell string) and optional stdin bytes. The operator's only way to
/// reach <c>cryptsetup</c>, <c>mount</c> and the rest, so tests record the exact arguments.
/// </summary>
internal interface ICommandRunner
{
    ValueTask<CommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments, ReadOnlyMemory<byte> standardInput, CancellationToken cancellationToken);
}

internal sealed class ProcessCommandRunner : ICommandRunner
{
    private const int _maxOutputBytes = 64 * 1024;

    public async ValueTask<CommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments, ReadOnlyMemory<byte> standardInput, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(fileName)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);

        // Stable English messages for the few places that read stderr.
        info.Environment["LC_ALL"] = "C";

        using var process = new Process { StartInfo = info };
        process.Start();
        try
        {
            var stdout = ReadBoundedAsync(process.StandardOutput, cancellationToken);
            var stderr = ReadBoundedAsync(process.StandardError, cancellationToken);

            // Stdin carries the volume key. It is written once and closed, so cryptsetup sees end of input.
            try
            {
                if (!standardInput.IsEmpty)
                    await process.StandardInput.BaseStream.WriteAsync(standardInput, cancellationToken).ConfigureAwait(false);
                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // The program exited without reading its input; its exit code says why.
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return new CommandResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            throw;
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (builder.Length < _maxOutputBytes)
                builder.Append(buffer, 0, Math.Min(read, _maxOutputBytes - builder.Length));
        }

        return builder.ToString();
    }
}
