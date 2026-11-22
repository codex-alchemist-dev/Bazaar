using Bazaar.Server.VmDensity.Abstractions;

namespace Bazaar.Server.VmDensity;

/// <summary>The planner's answer for one request.</summary>
public abstract record AdmissionPlan
{
    private AdmissionPlan()
    {
    }

    /// <summary>The VM fits now.</summary>
    public sealed record Admit : AdmissionPlan;

    /// <summary>Save these idle VMs, in order, then admit.</summary>
    /// <param name="Victims">The VMs to save, in the order to save them.</param>
    public sealed record SaveThenAdmit(IReadOnlyList<RunningVm> Victims) : AdmissionPlan;

    /// <summary>The VM does not fit and nothing more can be saved now; wait.</summary>
    public sealed record Wait : AdmissionPlan;

    /// <summary>The VM can never fit.</summary>
    /// <param name="Reason">Why.</param>
    public sealed record Reject(DensityAdmissionFailure Reason) : AdmissionPlan;
}

/// <summary>
/// The pure admission rules (doc 59 section 8, item 1). No clocks, no I/O: everything arrives as arguments, so the rules are
/// table-testable. The governor calls it under its state lock.
/// </summary>
public static class DensityPlanner
{
    /// <summary>Decides what to do with a start request.</summary>
    /// <param name="request">The VM to start.</param>
    /// <param name="host">Host memory just read.</param>
    /// <param name="reservedMb">Memory reserved by admissions not yet reported started (not by the request itself).</param>
    /// <param name="reservedCount">How many such reservations exist.</param>
    /// <param name="running">Running VMs that are save candidates or just running; excludes VMs already being saved.</param>
    /// <param name="options">The settings.</param>
    /// <param name="now">The current time.</param>
    /// <param name="pendingFreeMb">Memory of VMs whose save is already in flight (it will come back but has not yet).</param>
    /// <param name="pendingCount">How many saves are in flight; those VMs are still running but not in <paramref name="running"/>.</param>
    public static AdmissionPlan Plan(
        DensityVm request,
        HostMemory host,
        long reservedMb,
        int reservedCount,
        IReadOnlyList<RunningVm> running,
        VmDensityOptions options,
        DateTimeOffset now,
        long pendingFreeMb = 0,
        int pendingCount = 0)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(running);
        ArgumentNullException.ThrowIfNull(options);

        long need = request.MemoryMb;
        if (need > host.TotalMb - options.HostReserveMb)
            return new AdmissionPlan.Reject(DensityAdmissionFailure.TooLargeForHost);

        long shortfall = need - (host.FreeMb - reservedMb - options.HostReserveMb);

        // Count pressure: VMs that would be running once this one starts, not counting saves already in flight.
        int countVictims = options.MaxRunning > 0 ? running.Count + reservedCount + 1 - options.MaxRunning : 0;
        bool countFitsNow = options.MaxRunning <= 0 || running.Count + pendingCount + reservedCount + 1 <= options.MaxRunning;

        if (shortfall <= 0 && countFitsNow)
            return new AdmissionPlan.Admit();

        long memoryToFree = shortfall - pendingFreeMb;
        if (memoryToFree <= 0 && countVictims <= 0)
            return new AdmissionPlan.Wait(); // pending saves will cover it; wait for them

        var ordered = OrderCandidates(request, running, options, now);
        var victims = new List<RunningVm>();
        long freed = 0;
        foreach (var candidate in ordered)
        {
            if (freed >= memoryToFree && victims.Count >= countVictims)
                break;
            if (victims.Count >= options.MaxSavesPerAdmission)
                return new AdmissionPlan.Wait();
            victims.Add(candidate);
            freed += candidate.Vm.MemoryMb;
        }

        if (freed < memoryToFree || victims.Count < countVictims)
            return new AdmissionPlan.Wait();
        return new AdmissionPlan.SaveThenAdmit(victims);
    }

    /// <summary>
    /// Save candidates in the order to use them: other agents' idle VMs (Windows first, then Linux, least recently used first, VmId as
    /// the tie-break), then the requester's own idle VMs in the same order as a last resort.
    /// </summary>
    internal static List<RunningVm> OrderCandidates(DensityVm request, IReadOnlyList<RunningVm> running, VmDensityOptions options, DateTimeOffset now)
    {
        bool IsCandidate(RunningVm r) =>
            r.CanSaveState
            && !r.Activity.IsBusy
            && r.Vm.VmId != request.VmId
            && now - r.Activity.LastUsedAt >= options.MinIdleBeforeSave;

        IEnumerable<RunningVm> Sorted(IEnumerable<RunningVm> source) => source
            .OrderBy(r => r.Vm.Os == DensityGuestOs.Windows ? 0 : 1)
            .ThenBy(r => r.Activity.LastUsedAt)
            .ThenBy(r => r.Vm.VmId);

        var candidates = running.Where(IsCandidate).ToList();
        bool Own(RunningVm r) => string.Equals(r.Vm.AgentId, request.AgentId, StringComparison.Ordinal);
        return [.. Sorted(candidates.Where(r => !Own(r))), .. Sorted(candidates.Where(Own))];
    }
}
