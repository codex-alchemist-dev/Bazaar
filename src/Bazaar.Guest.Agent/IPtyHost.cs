using System.Threading.Channels;

namespace Bazaar.Guest.Agent;

/// <summary>Starts terminal sessions. The Linux implementation is <see cref="PipePtyHost"/>; tests use a fake.</summary>
public interface IPtyHost
{
    /// <summary>Starts <paramref name="shellPath"/> in a new terminal of the given size.</summary>
    IPtySession Open(string shellPath, int cols, int rows);
}

/// <summary>One terminal session. Disposing it ends the shell.</summary>
public interface IPtySession : IAsyncDisposable
{
    /// <summary>Terminal output in order. Completes when the shell ends.</summary>
    ChannelReader<byte[]> Output { get; }

    /// <summary>The shell's exit code. Completes when the shell ends, and never faults.</summary>
    Task<int> Exited { get; }

    /// <summary>Writes terminal input.</summary>
    ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct);

    /// <summary>Changes the terminal size.</summary>
    void Resize(int cols, int rows);
}
