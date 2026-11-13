using System.Threading.Channels;
using Bazaar.Conduit.Cloudflared;

namespace Bazaar.Conduit.Tests.Cloudflared;

/// <summary>A cloudflared process that the test drives: it prints lines and exits when told to.</summary>
internal sealed class FakeCloudflaredProcess : ICloudflaredProcess
{
    private readonly Channel<string> _stdout = Channel.CreateUnbounded<string>();
    private readonly Channel<string> _stderr = Channel.CreateUnbounded<string>();
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>True once <see cref="Kill"/> was called.</summary>
    public bool Killed { get; private set; }

    public IAsyncEnumerable<string> StandardOutputLines => _stdout.Reader.ReadAllAsync();

    public IAsyncEnumerable<string> StandardErrorLines => _stderr.Reader.ReadAllAsync();

    public Task<int> Exited => _exited.Task;

    public void Kill()
    {
        Killed = true;
        Exit(-9);
    }

    /// <summary>Prints a line on standard output.</summary>
    public void WriteStdout(string line) => _stdout.Writer.TryWrite(line);

    /// <summary>Prints a line on standard error.</summary>
    public void WriteStderr(string line) => _stderr.Writer.TryWrite(line);

    /// <summary>Ends the process with the given exit code.</summary>
    public void Exit(int code)
    {
        _stdout.Writer.TryComplete();
        _stderr.Writer.TryComplete();
        _exited.TrySetResult(code);
    }
}
