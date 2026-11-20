namespace Bazaar.Server.VmDensity.Abstractions;

/// <summary>
/// Admission controller in front of VM start (doc 59 section 8, item 1). It decides whether a VM may start now, saves idle VMs to make
/// room, or queues the start. It is an optimisation, not a security control: when disabled, or when the host registered no probe, it
/// admits everything.
/// </summary>
public interface IDensityGovernor
{
    /// <summary>
    /// Waits until the VM may start, saving idle VMs if needed, and returns a ticket that holds a memory reservation. The caller starts
    /// the VM, then calls <see cref="AdmissionTicket.ConfirmStartedAsync"/>, or disposes the ticket if the start failed.
    /// </summary>
    /// <param name="vm">The VM to start.</param>
    /// <param name="cancellationToken">Cancels the wait; any reservation is released.</param>
    /// <exception cref="DensityAdmissionException">The queue is full, the wait timed out, or the VM is larger than the host allows.</exception>
    Task<AdmissionTicket> AdmitAsync(DensityVm vm, CancellationToken cancellationToken);

    /// <summary>The current state of the governor.</summary>
    DensitySnapshot Snapshot();

    /// <summary>Tells the governor host memory changed (a VM stopped or was saved), so queued requests are re-evaluated at once.</summary>
    void NotifyMemoryChanged();
}

/// <summary>
/// A memory reservation held from admission until the VM is reported started or the start fails. The host's free memory does not drop
/// until the VM really boots, so the reservation stops two concurrent admissions spending the same free memory.
/// </summary>
public abstract class AdmissionTicket : IAsyncDisposable
{
    /// <summary>Reports the VM started: the reservation is released (the host's own free memory now reflects the VM). Idempotent.</summary>
    public abstract Task ConfirmStartedAsync();

    /// <summary>Releases the reservation if it is still held (the start failed or was abandoned) and re-evaluates the queue. Idempotent.</summary>
    public abstract ValueTask DisposeAsync();
}

/// <summary>Why an admission failed.</summary>
public enum DensityAdmissionFailure
{
    /// <summary>The request waited longer than the queue timeout.</summary>
    QueueTimeout,

    /// <summary>The queue already holds the maximum number of requests.</summary>
    QueueFull,

    /// <summary>The VM needs more memory than the host can ever offer after its reserve.</summary>
    TooLargeForHost,
}

/// <summary>Thrown by <see cref="IDensityGovernor.AdmitAsync"/> when a VM cannot be admitted.</summary>
public sealed class DensityAdmissionException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="reason">Why admission failed.</param>
    /// <param name="message">Detail for logs and the caller.</param>
    public DensityAdmissionException(DensityAdmissionFailure reason, string message)
        : base(message)
    {
        Reason = reason;
    }

    /// <summary>Why admission failed.</summary>
    public DensityAdmissionFailure Reason { get; }
}
