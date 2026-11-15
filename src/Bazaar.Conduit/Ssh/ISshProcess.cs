namespace Bazaar.Conduit.Ssh;

/// <summary>A running ssh child process, seen through its exit and the lines it prints on standard error.</summary>
public interface ISshProcess
{
    /// <summary>Lines written to standard error. Completes when the process closes the stream.</summary>
    IAsyncEnumerable<string> StandardErrorLines { get; }

    /// <summary>Completes with the exit code once the process has exited.</summary>
    Task<int> Exited { get; }

    /// <summary>Stops the process and its children. Safe to call after the process has exited.</summary>
    void Kill();
}
