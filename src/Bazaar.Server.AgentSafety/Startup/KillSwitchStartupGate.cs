using Bazaar.Server.Security.KillSwitch;
using Microsoft.Extensions.Hosting;

namespace Bazaar.Server.AgentSafety.Startup;

/// <summary>
/// Startup gate (owner decision): agents do not start until the stored kill switch state can be read. It is registered before the
/// job poller, the agent components and the responder, so none of them runs while the gate is closed. If the store cannot be read,
/// <see cref="StartAsync"/> throws and the host does not start, so nothing runs without a known stop state.
/// </summary>
public sealed class KillSwitchStartupGate(KillSwitchEngine engine) : IHostedService
{
    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) => engine.WaitUntilStateLoadedAsync(cancellationToken);

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
