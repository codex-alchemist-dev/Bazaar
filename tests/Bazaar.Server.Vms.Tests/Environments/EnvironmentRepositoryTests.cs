using Bazaar.Server.Vms.Environments;

namespace Bazaar.Server.Vms.Tests.Environments;

/// <summary>Repository round trips and state changes against a real PostgreSQL.</summary>
public sealed class EnvironmentRepositoryTests(VmDb db) : IClassFixture<VmDb>
{
    private static readonly DateTimeOffset _later = VmDb.Epoch.AddMinutes(5);

    [Fact]
    public async Task Golden_image_round_trips_and_changes_state()
    {
        var image = await db.InsertImageAsync(VmOs.Windows, version: 1, state: GoldenImageState.Building);

        Assert.Equal(image, await db.Repository.GetGoldenImageAsync(image.Id, TestContext.Current.CancellationToken));

        Assert.True(await db.Repository.SetGoldenImageStateAsync(image.Id, GoldenImageState.Ready, TestContext.Current.CancellationToken));
        var read = await db.Repository.GetGoldenImageAsync(image.Id, TestContext.Current.CancellationToken);
        Assert.Equal(GoldenImageState.Ready, read!.State);

        Assert.False(await db.Repository.SetGoldenImageStateAsync(Guid.CreateVersion7(), GoldenImageState.Retired, TestContext.Current.CancellationToken));
        Assert.Null(await db.Repository.GetGoldenImageAsync(Guid.CreateVersion7(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Vm_round_trips_with_null_backend_ref_until_it_is_set()
    {
        var image = await db.InsertImageAsync();
        var agent = Guid.CreateVersion7();
        var vm = await db.InsertVmAsync(agent, image.Id, state: VmState.Absent);

        var read = await db.Repository.GetVmAsync(vm.Id, TestContext.Current.CancellationToken);
        Assert.Equal(vm, read);
        Assert.Null(read!.BackendRef);

        Assert.True(await db.Repository.SetBackendRefAsync(vm.Id, "hyperv:abc", _later, TestContext.Current.CancellationToken));
        read = await db.Repository.GetVmAsync(vm.Id, TestContext.Current.CancellationToken);
        Assert.Equal("hyperv:abc", read!.BackendRef);
        Assert.Equal(_later, read.UpdatedAt);

        Assert.False(await db.Repository.SetBackendRefAsync(Guid.CreateVersion7(), "x", _later, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task List_non_retired_excludes_retired_and_list_for_agent_includes_retired()
    {
        var image = await db.InsertImageAsync();
        var agent = Guid.CreateVersion7();
        var live = await db.InsertVmAsync(agent, image.Id, VmOs.Linux, VmState.Running);
        var retired = await db.InsertVmAsync(agent, image.Id, VmOs.Windows, VmState.Retired);

        var nonRetired = await db.Repository.ListNonRetiredVmsAsync(TestContext.Current.CancellationToken);
        Assert.Contains(nonRetired, v => v.Id == live.Id);
        Assert.DoesNotContain(nonRetired, v => v.Id == retired.Id);
        Assert.All(nonRetired, v => Assert.NotEqual(VmState.Retired, v.State));

        var forAgent = await db.Repository.ListVmsForAgentAsync(agent, TestContext.Current.CancellationToken);
        Assert.Equal(new HashSet<Guid> { live.Id, retired.Id }, forAgent.Select(v => v.Id).ToHashSet());
        Assert.Empty(await db.Repository.ListVmsForAgentAsync(Guid.CreateVersion7(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Try_update_vm_state_succeeds_on_match_and_fails_on_stale_expected_state()
    {
        var image = await db.InsertImageAsync();
        var vm = await db.InsertVmAsync(Guid.CreateVersion7(), image.Id, state: VmState.Stopped);

        Assert.True(await db.Repository.TryUpdateVmStateAsync(vm.Id, VmState.Stopped, VmState.Running, _later, TestContext.Current.CancellationToken));
        var read = await db.Repository.GetVmAsync(vm.Id, TestContext.Current.CancellationToken);
        Assert.Equal(VmState.Running, read!.State);
        Assert.Equal(_later, read.UpdatedAt);

        // The caller still believes the VM is Stopped, so the compare-and-set must refuse.
        Assert.False(await db.Repository.TryUpdateVmStateAsync(vm.Id, VmState.Stopped, VmState.Saved, _later, TestContext.Current.CancellationToken));
        Assert.Equal(VmState.Running, (await db.Repository.GetVmAsync(vm.Id, TestContext.Current.CancellationToken))!.State);

        Assert.False(await db.Repository.TryUpdateVmStateAsync(Guid.CreateVersion7(), VmState.Stopped, VmState.Running, _later, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Checkpoints_round_trip_and_list_oldest_first()
    {
        var image = await db.InsertImageAsync();
        var vm = await db.InsertVmAsync(Guid.CreateVersion7(), image.Id);

        var newer = new EnvironmentCheckpointRecord(Guid.CreateVersion7(), vm.Id, "snap:2", null, VmDb.Epoch.AddHours(2));
        var older = new EnvironmentCheckpointRecord(Guid.CreateVersion7(), vm.Id, "snap:1", "before install", VmDb.Epoch);
        await db.Repository.InsertCheckpointAsync(newer, TestContext.Current.CancellationToken);
        await db.Repository.InsertCheckpointAsync(older, TestContext.Current.CancellationToken);

        var listed = await db.Repository.ListCheckpointsAsync(vm.Id, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { older, newer }, listed);
        Assert.Empty(await db.Repository.ListCheckpointsAsync(Guid.CreateVersion7(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Mount_detach_sets_detached_at_once_and_list_keeps_detached_rows()
    {
        var image = await db.InsertImageAsync();
        var vm = await db.InsertVmAsync(Guid.CreateVersion7(), image.Id);
        var mount = new ScopedMountRecord(
            Guid.CreateVersion7(), Guid.CreateVersion7(), vm.Id, MountKind.Workspace,
            "/srv/bazaar/ws", "/home/agent/ws", MountMode.Rw, null);
        await db.Repository.InsertMountAsync(mount, TestContext.Current.CancellationToken);

        Assert.Equal(new[] { mount }, await db.Repository.ListMountsAsync(vm.Id, TestContext.Current.CancellationToken));

        Assert.True(await db.Repository.DetachMountAsync(mount.Id, _later, TestContext.Current.CancellationToken));
        Assert.False(await db.Repository.DetachMountAsync(mount.Id, _later.AddMinutes(1), TestContext.Current.CancellationToken));

        var listed = await db.Repository.ListMountsAsync(vm.Id, TestContext.Current.CancellationToken);
        Assert.Equal(mount with { DetachedAt = _later }, Assert.Single(listed));
    }

    [Fact]
    public async Task Exec_log_insert_then_complete_fills_exit_code_and_output_size_once()
    {
        var image = await db.InsertImageAsync();
        var vm = await db.InsertVmAsync(Guid.CreateVersion7(), image.Id, state: VmState.Running);
        var hash = new string('b', 64);

        var id = await db.Repository.InsertExecLogAsync(new NewExecLog(vm.Id, VmDb.Epoch, hash, 900), TestContext.Current.CancellationToken);
        var open = await db.Repository.GetExecLogAsync(id, TestContext.Current.CancellationToken);
        Assert.Equal(new ExecLogRecord(id, vm.Id, VmDb.Epoch, hash, 900, null, null), open);

        Assert.True(await db.Repository.CompleteExecLogAsync(id, 0, 4096, TestContext.Current.CancellationToken));
        var done = await db.Repository.GetExecLogAsync(id, TestContext.Current.CancellationToken);
        Assert.Equal(new ExecLogRecord(id, vm.Id, VmDb.Epoch, hash, 900, 0, 4096), done);

        Assert.False(await db.Repository.CompleteExecLogAsync(id, 1, 1, TestContext.Current.CancellationToken));
        Assert.Equal(done, await db.Repository.GetExecLogAsync(id, TestContext.Current.CancellationToken));
        Assert.Null(await db.Repository.GetExecLogAsync(id + 1000, TestContext.Current.CancellationToken));
    }
}
