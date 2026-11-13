using System.Diagnostics;
using System.Threading.Channels;

namespace Bazaar.Conduit.Ssh;

/// <summary>Wraps a started <see cref="Process"/>. Standard output is discarded, standard error is read into a channel.</summary>
internal sealed class SystemSshProcess : ISshProcess
{
    private readonly Process _process;
    private readonly Channel<string> _stderr = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task<int> _exited;

    public SystemSshProcess(Process process)
    {
        _process = process;
        process.StandardInput.Close();
        var stdout = Task.Run(() => DiscardAsync(process.StandardOutput));
        var stderr = Task.Run(() => PumpAsync(process.StandardError, _stderr.Writer));
        _exited = Task.Run(async () =>
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            var code = process.ExitCode;
            process.Dispose();
            return code;
        });
    }

    /// <inheritdoc />
    public IAsyncEnumerable<string> StandardErrorLines => _stderr.Reader.ReadAllAsync();

    /// <inheritdoc />
    public Task<int> Exited => _exited;

    /// <inheritdoc />
    public void Kill()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited between the check and the kill.
        }
    }

    private static async Task DiscardAsync(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is not null)
            {
                // ssh -N prints nothing on success; anything it does print is dropped.
            }
        }
        catch (IOException)
        {
            // The stream closed under us; the exit path reports the outcome.
        }
    }

    private static async Task PumpAsync(StreamReader reader, ChannelWriter<string> writer)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                writer.TryWrite(line);
            }
        }
        catch (IOException)
        {
            // The stream closed under us; the exit path reports the outcome.
        }
        finally
        {
            writer.TryComplete();
        }
    }
}
