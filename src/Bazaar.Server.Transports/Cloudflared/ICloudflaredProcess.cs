namespace Bazaar.Server.Transports.Cloudflared;

/// <summary>A running cloudflared child process, seen through the lines it prints. Doc 39 section 2.</summary>
public interface ICloudflaredProcess
{
    /// <summary>Lines written to standard output. Completes when the process closes the stream.</summary>
    IAsyncEnumerable<string> StandardOutputLines { get; }

    /// <summary>Lines written to standard error. Completes when the process closes the stream.</summary>
    IAsyncEnumerable<string> StandardErrorLines { get; }

    /// <summary>Completes with the exit code once the process has exited.</summary>
    Task<int> Exited { get; }

    /// <summary>Stops the process and its children. Safe to call after the process has exited.</summary>
    void Kill();
}
