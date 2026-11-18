using System.Globalization;

namespace Bazaar.Server.Valkey;

/// <summary>Everything the supervisor needs to start a managed <c>valkey-server</c> child on loopback.</summary>
/// <param name="Executable">The server binary, from <see cref="ValkeyCacheOptions.BinaryPath"/>.</param>
/// <param name="Port">The loopback TCP port to listen on.</param>
public sealed record ValkeyLaunchSpec(string Executable, int Port)
{
    /// <summary>
    /// The command line arguments. The server binds to loopback only, keeps protected mode on, and writes no snapshot or append-only
    /// file: the cache is rebuildable (doc 11), so nothing is persisted. No secret is passed on the command line.
    /// </summary>
    public IReadOnlyList<string> Arguments =>
    [
        "--bind", "127.0.0.1",
        "--port", Port.ToString(CultureInfo.InvariantCulture),
        "--protected-mode", "yes",
        "--save", string.Empty,
        "--appendonly", "no",
    ];
}

/// <summary>Starts a managed child process. The real implementation runs a process; tests inject a fake.</summary>
public interface IValkeyProcessLauncher
{
    /// <summary>Starts the child. Throws when the executable cannot be started; the supervisor treats that as a failed attempt.</summary>
    Task<IValkeyChild> StartAsync(ValkeyLaunchSpec spec, CancellationToken ct);
}

/// <summary>A running managed child.</summary>
public interface IValkeyChild : IAsyncDisposable
{
    /// <summary>Completes when the child exits, for any reason.</summary>
    Task Exited { get; }

    /// <summary>Stops the child if it is still running. Never throws.</summary>
    void Kill();
}
