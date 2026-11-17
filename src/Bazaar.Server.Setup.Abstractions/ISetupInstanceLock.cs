namespace Bazaar.Server.Setup;

/// <summary>
/// The instance lock that apply runs under (doc 45 section 1.2, doc 03). Acquiring it returns a lease, and disposing the lease
/// releases it. The Host supplies the implementation over its advisory lock, so this interface keeps Bazaar.Server platform-neutral.
/// </summary>
public interface ISetupInstanceLock
{
    /// <summary>Takes the lock without waiting. Throws <see cref="SetupInstanceLockHeldException"/> when another holder has it.</summary>
    Task<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken = default);
}

/// <summary>Thrown by <see cref="ISetupInstanceLock.AcquireAsync"/> when another process holds the instance lock.</summary>
public sealed class SetupInstanceLockHeldException() : Exception("The instance lock is held by another process.");
