using Bazaar.Server.PsychWard.Abstractions;
using Bazaar.Server.Security.KillSwitch;
using Bazaar.Server.VmMounts.Abstractions;

namespace Bazaar.Server.AgentSafety.Containment;

/// <summary>
/// Agent safety's <see cref="ICanarySink"/> (doc 15, "Credentials, mounts, browser", PROPOSED there): any use of a planted canary
/// freezes the agent through the kill switch, whose engage record is the page to the owner (there is no separate pager yet), and
/// then tells the Psych Ward (doc 26: a canary hit is a deterministic trigger). The freeze comes first and its failure is thrown,
/// so the mount manager surfaces it: a canary report is never dropped. The ward report is best effort and never throws. The report
/// carries ids and a fingerprint only; the canary value is never held here.
/// </summary>
public sealed class CanaryContainmentSink(KillSwitchEngine killSwitch, IWardTriggerIntake? ward = null) : ICanarySink
{
    /// <inheritdoc />
    public async ValueTask ReportCanaryUseAsync(CanaryUseReport report, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(report);

        var reason = $"canary {report.Kind} (fingerprint {Short(report.CanaryId)}, source {report.Source}{(report.Retired ? ", after detach" : string.Empty)})";
        await killSwitch.ReportBreachAsync(report.AgentId.ToString("D"), reason, ct).ConfigureAwait(false);

        if (ward is null)
        {
            return;
        }

        try
        {
            await ward.ReportCanaryUseAsync(report.AgentId, $"{report.Kind} via {report.Source}", ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Reporting is best effort.
        }
    }

    private static string Short(string fingerprint) => fingerprint.Length <= 12 ? fingerprint : fingerprint[..12];
}
