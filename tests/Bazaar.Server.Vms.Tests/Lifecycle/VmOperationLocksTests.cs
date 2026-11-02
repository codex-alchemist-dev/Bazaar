using Bazaar.Server.Vms.Lifecycle;

namespace Bazaar.Server.Vms.Tests.Lifecycle;

/// <summary>The per-VM lock on its own, without the lifecycle service.</summary>
public sealed class VmOperationLocksTests
{
    [Fact]
    public async Task Second_acquire_on_the_same_vm_waits_until_the_first_is_released()
    {
        var locks = new VmOperationLocks();
        var vm = Guid.CreateVersion7();

        var first = await locks.AcquireAsync(vm, TestContext.Current.CancellationToken);
        var second = locks.AcquireAsync(vm, TestContext.Current.CancellationToken);

        // The lock is held, so the waiter cannot have completed.
        Assert.False(second.IsCompleted);

        first.Dispose();
        using var secondLease = await second;
        Assert.Equal(1, locks.ActiveCount);
    }

    [Fact]
    public async Task Acquires_on_different_vms_do_not_block_each_other()
    {
        var locks = new VmOperationLocks();

        using var a = await locks.AcquireAsync(Guid.CreateVersion7(), TestContext.Current.CancellationToken);
        using var b = await locks.AcquireAsync(Guid.CreateVersion7(), TestContext.Current.CancellationToken);

        Assert.Equal(2, locks.ActiveCount);
    }

    [Fact]
    public async Task Cancelled_wait_throws_takes_no_lock_and_leaves_no_entry()
    {
        var locks = new VmOperationLocks();
        var vm = Guid.CreateVersion7();
        using var cts = new CancellationTokenSource();

        var holder = await locks.AcquireAsync(vm, TestContext.Current.CancellationToken);
        var waiter = locks.AcquireAsync(vm, cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => waiter);
        holder.Dispose();

        Assert.Equal(0, locks.ActiveCount);
    }

    [Fact]
    public async Task Entries_are_removed_when_the_last_user_leaves()
    {
        var locks = new VmOperationLocks();
        var vm = Guid.CreateVersion7();

        for (var i = 0; i < 3; i++)
        {
            using var lease = await locks.AcquireAsync(vm, TestContext.Current.CancellationToken);
            Assert.Equal(1, locks.ActiveCount);
        }

        Assert.Equal(0, locks.ActiveCount);
    }

    [Fact]
    public async Task Disposing_a_lease_twice_releases_once()
    {
        var locks = new VmOperationLocks();
        var vm = Guid.CreateVersion7();

        var lease = await locks.AcquireAsync(vm, TestContext.Current.CancellationToken);
        lease.Dispose();
        lease.Dispose();

        using var next = await locks.AcquireAsync(vm, TestContext.Current.CancellationToken);
        Assert.Equal(1, locks.ActiveCount);
    }
}
