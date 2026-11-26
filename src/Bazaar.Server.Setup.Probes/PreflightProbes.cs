using Bazaar.Kodo.Probes;
using Bazaar.Kodo.Probes.Host;

namespace Bazaar.Server.Setup.Probes;

/// <summary><c>host.os</c>: the server runs on Windows or Linux, 64-bit (CLAUDE.md, owner 2026-10-24). Anything else is not a supported host.</summary>
public sealed class HostOsProbe(IHostFacts facts) : ISetupInfrastructureProbe
{
    /// <inheritdoc />
    public string CheckId => "host.os";

    /// <inheritdoc />
    public Task<SetupProbeReading> ProbeAsync(SetupVerificationContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var reading = (facts.IsWindows || facts.IsLinux, facts.Is64Bit) switch
        {
            (false, _) => new SetupProbeReading(SetupProbeOutcome.Failed, "This operating system is not a supported server host.", "Run the server on Windows or Linux."),
            (true, false) => new SetupProbeReading(SetupProbeOutcome.Failed, "The server needs a 64-bit operating system.", "Use a 64-bit install."),
            _ => new SetupProbeReading(SetupProbeOutcome.Healthy, facts.IsWindows ? "Windows, 64-bit." : "Linux, 64-bit."),
        };
        return Task.FromResult(reading);
    }
}

/// <summary>
/// <c>host.ports</c>: the status port of the plan is free to listen on. A port already in use is amber, not red: it is expected when
/// this very server is already running (a re-verify), and a real clash shows when the server starts.
/// </summary>
public sealed class HostPortsProbe(IHostFacts facts) : ISetupInfrastructureProbe
{
    /// <inheritdoc />
    public string CheckId => "host.ports";

    /// <inheritdoc />
    public Task<SetupProbeReading> ProbeAsync(SetupVerificationContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var port = context.Plan.Network.StatusPort;
        if (port is < 1 or > 65535)
        {
            return Task.FromResult(new SetupProbeReading(SetupProbeOutcome.Failed, "The status port is outside 1 to 65535.", "Set network.statusPort to a valid port."));
        }

        return Task.FromResult(facts.ListeningTcpPorts().Contains(port)
            ? new SetupProbeReading(
                SetupProbeOutcome.Degraded,
                "The status port is already in use. That is expected when this server is already running.",
                "If no server of this install is running, pick another network.statusPort.")
            : new SetupProbeReading(SetupProbeOutcome.Healthy, "The status port is free."));
    }
}

/// <summary>
/// <c>host.dataRoot</c>: the data root exists and is not marked read-only. Read-only on purpose (doc 45 section 1.12): this does not
/// create a file, so real write access is proven by the first write. A root that does not exist yet is amber: apply creates it.
/// </summary>
public sealed class HostDataRootProbe(IHostFacts facts) : ISetupInfrastructureProbe
{
    /// <inheritdoc />
    public string CheckId => "host.dataRoot";

    /// <inheritdoc />
    public Task<SetupProbeReading> ProbeAsync(SetupVerificationContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var root = DataRoot.Resolve(context.Plan, facts);
        if (!facts.DirectoryExists(root))
        {
            return Task.FromResult(new SetupProbeReading(
                SetupProbeOutcome.Degraded, "The data root does not exist yet. Setup creates it when it applies.", "Create the folder, or let apply create it."));
        }

        return Task.FromResult(facts.IsMarkedReadOnly(root)
            ? new SetupProbeReading(SetupProbeOutcome.Failed, "The data root is marked read-only.", "Clear the read-only attribute, or move the data root.")
            : new SetupProbeReading(SetupProbeOutcome.Healthy, "The data root exists and is not marked read-only. Write access is proven by the first write."));
    }
}

/// <summary><c>host.disk</c>: Kodo's <see cref="DiskFreeProbe"/> on the volume that holds the plan's data root (doc 45 section 1.12).</summary>
public sealed class HostDiskProbe(IHostFacts facts, IHostReadings readings) : ISetupInfrastructureProbe
{
    /// <inheritdoc />
    public string CheckId => "host.disk";

    /// <inheritdoc />
    public async Task<SetupProbeReading> ProbeAsync(SetupVerificationContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var volume = facts.VolumeOf(DataRoot.Resolve(context.Plan, facts));
        if (volume is null)
        {
            return new SetupProbeReading(SetupProbeOutcome.Unavailable, "The volume of the data root could not be found");
        }

        var result = await new DiskFreeProbe(readings, volume).RunAsync(cancellationToken).ConfigureAwait(false);
        const string fix = "Free space on the volume, or move the data root to a larger one.";
        return result.Status switch
        {
            ProbeStatus.Green => new SetupProbeReading(SetupProbeOutcome.Healthy, "Disk usage on the data volume is within its limits."),
            ProbeStatus.Amber => new SetupProbeReading(SetupProbeOutcome.Degraded, $"Disk usage on the data volume is past its warning limit ({result.Cause}).", fix),
            ProbeStatus.Red => new SetupProbeReading(SetupProbeOutcome.Failed, $"Disk usage on the data volume is past its hard limit ({result.Cause}).", fix),
            _ => new SetupProbeReading(SetupProbeOutcome.Unavailable, $"Disk usage on the data volume could not be read ({result.Cause})"),
        };
    }
}

internal static class DataRoot
{
    public static string Resolve(SetupPlan plan, IHostFacts facts) =>
        string.IsNullOrWhiteSpace(plan.Database.DataRoot) ? facts.DefaultDataRoot : plan.Database.DataRoot;
}
