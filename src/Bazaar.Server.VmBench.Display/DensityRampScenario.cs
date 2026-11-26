using Bazaar.Server.VmDensity.Abstractions;
using static Bazaar.Server.VmBench.Display.DisplayBenchSupport;

namespace Bazaar.Server.VmBench.Display;

/// <summary>
/// <see cref="DisplayBenchScenario.DensityRamp"/>: asks the density governor to admit a Windows and a Linux VM per pair until it
/// queues (an admission still pending after the detect window while the governor reports queued requests), refuses, or the pair
/// ceiling is reached. Records pairs admitted and host free memory at that point; passes when at least
/// <see cref="DisplayBenchCriteriaSnapshot.DensityMinPairs"/> pairs were admitted. No VM is started: the tickets hold the
/// governor's reservations, so each admission sees the memory of the ones before it, and all are released at the end.
/// </summary>
public sealed class DensityRampScenario : IDisplayBenchScenario
{
    private const string _variant = "pairs";
    private const string _unit = "pairs";
    private readonly IDensityGovernor? _governor;

    public DensityRampScenario(IDensityGovernor? governor = null)
    {
        _governor = governor;
    }

    public DisplayBenchScenario Id => DisplayBenchScenario.DensityRamp;

    public async Task<IReadOnlyList<DisplayBenchResult>> RunAsync(DisplayBenchContext context, CancellationToken ct)
    {
        if (_governor is null)
        {
            return [NotRun(Id, _variant, _unit, "No density governor is registered on this host.")];
        }

        var tickets = new List<AdmissionTicket>();
        try
        {
            var c = context.Criteria;
            var pairs = 0;
            var stop = $"The pair ceiling of {c.DensityMaxPairs} was reached without the governor queueing.";
            for (var i = 0; i < c.DensityMaxPairs; i++)
            {
                var windows = new DensityVm(Guid.NewGuid(), "bench-ramp-" + i, DensityGuestOs.Windows, c.DensityWindowsVmMemoryMb);
                var linux = new DensityVm(Guid.NewGuid(), "bench-ramp-" + i, DensityGuestOs.Linux, c.DensityLinuxVmMemoryMb);
                var windowsStop = await TryAdmitAsync(windows, context, tickets, ct);
                if (windowsStop is not null)
                {
                    stop = windowsStop;
                    break;
                }

                var linuxStop = await TryAdmitAsync(linux, context, tickets, ct);
                if (linuxStop is not null)
                {
                    stop = linuxStop;
                    break;
                }

                pairs++;
            }

            var freeMb = _governor.Snapshot().HostFreeMb;
            var outcome = pairs >= c.DensityMinPairs ? DisplayBenchOutcome.Passed : DisplayBenchOutcome.Failed;
            return [new DisplayBenchResult(Id, _variant, outcome, pairs, _unit, c.DensityMinPairs, null,
                [new DisplayBenchMetric("hostFreeMb", freeMb, "MiB")],
                outcome == DisplayBenchOutcome.Passed ? null : $"{pairs} pairs admitted, fewer than {c.DensityMinPairs}. {stop}")];
        }
        catch (Exception ex) when (!IsCallerCancellation(ex, ct))
        {
            return [Failed(Id, _variant, _unit, Describe(ex))];
        }
        finally
        {
            foreach (var ticket in tickets)
            {
                await ticket.DisposeAsync();
            }
        }
    }

    /// <summary>Admits one VM. Returns null when it was admitted, or the reason the ramp stops when it was queued or refused.</summary>
    private async Task<string?> TryAdmitAsync(DensityVm vm, DisplayBenchContext context, List<AdmissionTicket> tickets, CancellationToken ct)
    {
        var governor = _governor!;
        var queuedBefore = governor.Snapshot().Queued;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var detectCancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var detect = Task.Delay(TimeSpan.FromSeconds(context.Criteria.DensityQueueDetectSeconds), context.Time, detectCancel.Token);
        try
        {
            var admit = governor.AdmitAsync(vm, cancel.Token);
            if (await Task.WhenAny(admit, detect) == admit)
            {
                tickets.Add(await admit);
                return null;
            }

            ct.ThrowIfCancellationRequested();
            var queued = governor.Snapshot().Queued > queuedBefore;
            await cancel.CancelAsync();
            try
            {
                tickets.Add(await admit);
                return null;
            }
            catch (OperationCanceledException)
            {
                return queued ? "The governor queued the next admission." : "The next admission did not complete within the detect window.";
            }
        }
        catch (DensityAdmissionException ex)
        {
            return $"The governor refused the next admission ({ex.Reason}).";
        }
        finally
        {
            await detectCancel.CancelAsync();
        }
    }
}
