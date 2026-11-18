using Bazaar.Server.Vms.Environments;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Vms.Tests.Environments;

/// <summary>The partial unique index that allows one live VM per agent and OS, and the attached-mount guard.</summary>
public sealed class EnvironmentConstraintTests(VmDb db) : IClassFixture<VmDb>
{
    [Fact]
    public async Task Second_live_vm_for_the_same_agent_and_os_is_refused()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var image = await db.InsertImageAsync(VmOs.Linux);
        var agent = Guid.CreateVersion7();
        await db.InsertVmAsync(agent, image.Id, VmOs.Linux, VmState.Stopped);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => db.InsertVmAsync(agent, image.Id, VmOs.Linux, VmState.Running));
        Assert.Equal("23505", ex.SqlState);
        Assert.Equal("environment_vm_live_agent_os_idx", ex.ConstraintName);
    }

    [Fact]
    public async Task Same_agent_may_hold_one_live_vm_per_os()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var image = await db.InsertImageAsync(VmOs.Linux);
        var agent = Guid.CreateVersion7();
        await db.InsertVmAsync(agent, image.Id, VmOs.Linux, VmState.Stopped);
        var windows = await db.InsertVmAsync(agent, image.Id, VmOs.Windows, VmState.Stopped);

        Assert.Equal(windows, await db.Repository.GetVmAsync(windows.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Retiring_a_vm_frees_the_slot_for_a_new_vm()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var image = await db.InsertImageAsync(VmOs.Windows);
        var agent = Guid.CreateVersion7();
        var first = await db.InsertVmAsync(agent, image.Id, VmOs.Windows, VmState.Running);

        Assert.True(await db.Repository.TryUpdateVmStateAsync(first.Id, VmState.Running, VmState.Retired, VmDb.Epoch, TestContext.Current.CancellationToken));
        var replacement = await db.InsertVmAsync(agent, image.Id, VmOs.Windows, VmState.Absent);

        var live = await db.Repository.ListVmsForAgentAsync(agent, TestContext.Current.CancellationToken);
        Assert.Contains(live, v => v.Id == replacement.Id && v.State == VmState.Absent);
        Assert.Contains(live, v => v.Id == first.Id && v.State == VmState.Retired);
    }

    [Fact]
    public async Task Attached_guest_path_is_unique_until_its_mount_is_detached()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var image = await db.InsertImageAsync();
        var vm = await db.InsertVmAsync(Guid.CreateVersion7(), image.Id);
        var agent = Guid.CreateVersion7();
        var first = new ScopedMountRecord(Guid.CreateVersion7(), agent, vm.Id, MountKind.Soul, "/srv/soul", "/soul", MountMode.Ro, null);
        await db.Repository.InsertMountAsync(first, TestContext.Current.CancellationToken);

        var clash = new ScopedMountRecord(Guid.CreateVersion7(), agent, vm.Id, MountKind.Soul, "/srv/other", "/soul", MountMode.Ro, null);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => db.Repository.InsertMountAsync(clash, TestContext.Current.CancellationToken));
        Assert.Equal("23505", ex.SqlState);

        await db.Repository.DetachMountAsync(first.Id, VmDb.Epoch, TestContext.Current.CancellationToken);
        await db.Repository.InsertMountAsync(clash, TestContext.Current.CancellationToken);
        Assert.Equal(2, (await db.Repository.ListMountsAsync(vm.Id, TestContext.Current.CancellationToken)).Count);
    }
}
