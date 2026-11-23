using Bazaar.Server.VmDensity.Abstractions;
using Bazaar.Server.Vms.Abstractions;

namespace Bazaar.Server.VmDensity.Vms;

/// <summary>
/// Implements the density governor's VM ports over the VM module's read and control ports (doc 59 section 8, PROPOSED). Memory and the
/// save capability come from <see cref="AgentVmInfo"/>; activity comes from the <see cref="IVmActivityReader"/>.
/// </summary>
/// <remarks>
/// <para>
/// The rule for anything unknown is the cautious one, and it is never reported as idle on a guess. A VM whose activity cannot be read
/// (no reader, the reader returns nothing, throws, or is too slow) is reported with <see cref="VmActivity.Unknown"/>, which counts as
/// busy. A VM may be saved only when the backend said it can save state, memory is known, and a control port exists.
/// </para>
/// <para>
/// Failure follows the governor's contract: a directory failure (no directory, a failed or slow read) propagates, and the governor then
/// admits the start untracked. A failed save throws, and the governor skips that VM for a while. Cancellation by the caller propagates.
/// </para>
/// </remarks>
public sealed class VmDensityVmAdapter : IDensityVmDirectory, IDensityVmControl
{
    private readonly IAgentVmDirectory? _directory;
    private readonly IVmControl? _control;
    private readonly IVmActivityReader? _activity;
    private readonly TimeProvider _time;
    private readonly VmDensityVmsOptions _options;

    /// <summary>Creates the adapter. Every port is optional; a missing one only makes the adapter more cautious.</summary>
    /// <param name="directory">The VM module's read port.</param>
    /// <param name="control">The VM module's control port; absent when no VM backend is configured.</param>
    /// <param name="activity">Reads per-VM activity.</param>
    /// <param name="time">The clock and timer source.</param>
    /// <param name="options">The settings.</param>
    public VmDensityVmAdapter(
        IAgentVmDirectory? directory,
        IVmControl? control,
        IVmActivityReader? activity,
        TimeProvider time,
        VmDensityVmsOptions options)
    {
        _directory = directory;
        _control = control;
        _activity = activity;
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RunningVm>> ListRunningAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_directory is null)
            throw new InvalidOperationException("No VM directory is registered, so the running VMs are not known.");

        IReadOnlyList<AgentVmInfo> running = await _directory.ListRunningAsync(cancellationToken)
            .WaitAsync(Timeout, _time, cancellationToken)
            .ConfigureAwait(false);

        // A defensive filter: the directory promises Running entries with a backend reference, and only those are listed here.
        List<AgentVmInfo> vms = [.. running.Where(v => v.State == VmRunState.Running && !string.IsNullOrWhiteSpace(v.BackendRef))];
        DateTimeOffset now = _time.GetUtcNow();
        var tasks = new Task<RunningVm>[vms.Count];
        for (int i = 0; i < vms.Count; i++)
            tasks[i] = DescribeAsync(vms[i], now, cancellationToken);
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">
    /// There is no control port, the VM is not running, cannot be saved, or is not provably idle now, or the VM module did not apply the save.
    /// </exception>
    public async Task SaveAsync(Guid vmId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_control is null)
            throw new InvalidOperationException("No VM control port is registered, so no VM can be saved.");

        // The governor decided from a listing taken a moment ago. Read the VM again, so a VM that became busy or is no longer
        // running in between is never saved.
        IReadOnlyList<RunningVm> now = await ListRunningAsync(cancellationToken).ConfigureAwait(false);
        RunningVm? current = now.FirstOrDefault(r => r.Vm.VmId == vmId);
        if (current is null)
            throw new InvalidOperationException($"VM {vmId} is not running, so it is not saved.");
        if (!current.CanSaveState)
            throw new InvalidOperationException($"VM {vmId} cannot be saved: the backend's save capability is not confirmed or its memory is not known.");
        if (current.Activity.IsBusy)
            throw new InvalidOperationException($"VM {vmId} is busy or its activity could not be read, so it is not saved.");

        VmControlResult result = await _control.SaveAsync(vmId, cancellationToken).ConfigureAwait(false);
        if (!result.IsApplied)
            throw new InvalidOperationException($"VM {vmId} was not saved: {result.Outcome}.");
    }

    private TimeSpan Timeout => TimeSpan.FromSeconds(_options.ReadTimeoutSeconds);

    private async Task<RunningVm> DescribeAsync(AgentVmInfo info, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var vm = new DensityVm(
            info.VmId,
            info.AgentId.ToString("D"),
            info.Os == VmGuestOs.Windows ? DensityGuestOs.Windows : DensityGuestOs.Linux,
            Math.Max(info.MemoryMb, 0));
        VmActivity activity = await ReadActivityAsync(vm, now, cancellationToken).ConfigureAwait(false);
        bool canSave = _control is not null && info.CanSaveState == true && info.MemoryMb > 0;
        return new RunningVm(vm, activity, canSave);
    }

    private async Task<VmActivity> ReadActivityAsync(DensityVm vm, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (_activity is null)
            return VmActivity.Unknown(now);
        try
        {
            return await _activity.ReadAsync(vm, cancellationToken).WaitAsync(Timeout, _time, cancellationToken).ConfigureAwait(false)
                ?? VmActivity.Unknown(now);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // A reader that fails or is too slow tells nothing about the VM, so it counts as busy.
            return VmActivity.Unknown(now);
        }
    }
}
