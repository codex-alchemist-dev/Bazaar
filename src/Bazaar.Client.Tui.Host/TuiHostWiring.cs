using Bazaar.Client.Tui.Dashboard;
using Bazaar.Server.Setup;

namespace Bazaar.Client.Tui.Host;

/// <summary>What Ops mode runs on: the ports, the sign-in, the crash journal, and whether an owner exists. Disposing it closes the database.</summary>
public sealed class OpsWiring(OpsPorts ports, OpsSignIn signIn, IOpsCrashJournal crashes, Func<CancellationToken, Task<bool>> ownerExists, IAsyncDisposable? owner = null)
    : IAsyncDisposable
{
    public OpsPorts Ports => ports;

    public OpsSignIn SignIn => signIn;

    public IOpsCrashJournal Crashes => crashes;

    public Task<bool> OwnerExistsAsync(CancellationToken ct) => ownerExists(ct);

    public ValueTask DisposeAsync() => owner?.DisposeAsync() ?? ValueTask.CompletedTask;
}

/// <summary>What the installer applies through: the gate (open only when the durable audit store is reachable). Disposing it closes the database.</summary>
public sealed class InstallWiring(ICliApplyGate gate, IAsyncDisposable? owner = null) : IAsyncDisposable
{
    public ICliApplyGate Gate => gate;

    public ValueTask DisposeAsync() => owner?.DisposeAsync() ?? ValueTask.CompletedTask;
}

/// <summary>
/// The seam between the program and the server's pieces, so the command line is tested without a database. The real implementation is
/// <see cref="ServerTuiWiring"/>.
/// </summary>
public interface ITuiHostWiring
{
    /// <summary>The Ops pieces, or null when the server database cannot be reached (no connection reference, or it does not answer).</summary>
    Task<OpsWiring?> OpenOpsAsync(IReadOnlyDictionary<string, string?> variables, CancellationToken ct);

    Task<InstallWiring> OpenInstallAsync(IReadOnlyDictionary<string, string?> variables, CancellationToken ct);
}
