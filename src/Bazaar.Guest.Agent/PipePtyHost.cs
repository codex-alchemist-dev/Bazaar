using System.Diagnostics;
using System.Threading.Channels;
using Bazaar.Contracts.Vms;

namespace Bazaar.Guest.Agent;

/// <summary>
/// Linux shell host that runs the shell with redirected pipes. It is pipe-backed, NOT a real TTY: there is no
/// pseudo-terminal, so the shell sees no tty, line editing and window size do not apply, and
/// <see cref="IPtySession.Resize"/> is accepted and ignored. A real pty (openpty) is a later change behind the same
/// interface.
/// </summary>
public sealed class PipePtyHost : IPtyHost
{
    /// <inheritdoc />
    public IPtySession Open(string shellPath, int cols, int rows)
    {
        // cols and rows are unused: nothing can apply them to a pipe. Kept so a real pty can use them later.
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin",
            ["TERM"] = "dumb",
        };
        var (process, ownsGroup) = ChildProcess.Start([shellPath], null, environment);
        return new PipePtySession(process, ownsGroup);
    }
}

/// <summary>A shell whose stdin, stdout and stderr are pipes. Its output channel merges both streams.</summary>
internal sealed class PipePtySession : IPtySession
{
    private readonly Process _process;
    private readonly bool _ownsGroup;
    private readonly Channel<byte[]> _output = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(256)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.Wait,
    });

    /// <summary>Starts the output pumps for an already started shell.</summary>
    public PipePtySession(Process process, bool ownsGroup)
    {
        _process = process;
        _ownsGroup = ownsGroup;
        var stdout = PumpAsync(process.StandardOutput.BaseStream);
        var stderr = PumpAsync(process.StandardError.BaseStream);
        _ = Task.WhenAll(stdout, stderr).ContinueWith(_ => _output.Writer.TryComplete(), TaskScheduler.Default);
    }

    /// <inheritdoc />
    public ChannelReader<byte[]> Output => _output.Reader;

    /// <inheritdoc />
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        var input = _process.StandardInput.BaseStream;
        await input.WriteAsync(bytes, ct);
        await input.FlushAsync(ct);
    }

    /// <summary>No effect: a pipe has no window size. See the class remarks.</summary>
    public void Resize(int cols, int rows)
    {
        // Intentionally empty for the pipe-backed host.
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _output.Writer.TryComplete();
        ChildProcess.KillTree(_process, _ownsGroup);
        _process.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task PumpAsync(Stream source)
    {
        var buffer = new byte[OutboundQueue.MaxPayloadBytes];
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer);
                if (read == 0)
                {
                    return;
                }

                await _output.Writer.WriteAsync(buffer.AsSpan(0, read).ToArray());
            }
        }
        catch (IOException)
        {
            // The pipe closed when the shell ended or was disposed.
        }
        catch (ChannelClosedException)
        {
            // The session was disposed while output was still arriving.
        }
    }
}
