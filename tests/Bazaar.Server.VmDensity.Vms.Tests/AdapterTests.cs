using Bazaar.Server.VmDensity.Abstractions;
using Bazaar.Server.Vms.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.VmDensity.Vms.Tests;

public sealed class AdapterTests
{
    private readonly FakeTimeProvider _time = new(Vm.Epoch);

    private VmDensityVmAdapter Build(IAgentVmDirectory? directory, IVmControl? control, IVmActivityReader? reader, int timeoutSeconds = 5) =>
        new(directory, control, reader, _time, new VmDensityVmsOptions { ReadTimeoutSeconds = timeoutSeconds });

    private static FakeReader IdleReader() => new(_ => Task.FromResult<VmActivity?>(Vm.Idle()));

    [Fact]
    public async Task Facts_come_from_the_directory_and_the_agent_id_is_the_D_form()
    {
        var agent = Guid.CreateVersion7();
        var info = Vm.Running(agent: agent, os: VmGuestOs.Windows, memoryMb: 4096);
        var adapter = Build(new FakeDirectory(info), new FakeControl(), IdleReader());

        var listed = Assert.Single(await adapter.ListRunningAsync(TestContext.Current.CancellationToken));

        Assert.Equal(new DensityVm(info.VmId, agent.ToString("D"), DensityGuestOs.Windows, 4096), listed.Vm);
        Assert.True(listed.CanSaveState);
        Assert.False(listed.Activity.IsBusy);
        Assert.Equal(Vm.Epoch - TimeSpan.FromHours(1), listed.Activity.LastUsedAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public async Task A_save_capability_that_is_unknown_or_false_means_cannot_save(bool? canSave)
    {
        var adapter = Build(new FakeDirectory(Vm.Running(canSave: canSave)), new FakeControl(), IdleReader());

        Assert.False(Assert.Single(await adapter.ListRunningAsync(TestContext.Current.CancellationToken)).CanSaveState);
    }

    [Fact]
    public async Task Unknown_memory_or_a_missing_control_port_means_cannot_save()
    {
        var unknownMemory = Build(new FakeDirectory(Vm.Running(memoryMb: 0)), new FakeControl(), IdleReader());
        var noControl = Build(new FakeDirectory(Vm.Running()), null, IdleReader());

        Assert.False(Assert.Single(await unknownMemory.ListRunningAsync(TestContext.Current.CancellationToken)).CanSaveState);
        Assert.False(Assert.Single(await noControl.ListRunningAsync(TestContext.Current.CancellationToken)).CanSaveState);
    }

    [Fact]
    public async Task Without_a_reader_or_when_it_returns_nothing_fails_or_is_slow_the_vm_is_busy()
    {
        var infos = new[] { Vm.Running(), Vm.Running(), Vm.Running(), Vm.Running() };
        int call = 0;
        var reader = new FakeReader(vm => Interlocked.Increment(ref call) switch
        {
            1 => Task.FromResult<VmActivity?>(null),
            2 => Task.FromException<VmActivity?>(new InvalidOperationException("boom")),
            _ => new TaskCompletionSource<VmActivity?>().Task, // never completes
        });

        var withReader = Build(new FakeDirectory(infos), new FakeControl(), reader);
        var task = withReader.ListRunningAsync(TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromSeconds(6));
        var listed = await task;
        var withoutReader = await Build(new FakeDirectory(infos), new FakeControl(), null).ListRunningAsync(TestContext.Current.CancellationToken);

        Assert.Equal(4, listed.Count);
        Assert.All(listed, r => Assert.True(r.Activity.IsBusy && r.Activity.IsUnknown));
        Assert.All(withoutReader, r => Assert.True(r.Activity.IsBusy && r.Activity.IsUnknown));
    }

    [Fact]
    public async Task A_busy_reading_is_kept_as_it_is()
    {
        var reader = new FakeReader(_ => Task.FromResult<VmActivity?>(new VmActivity(true, false, false, Vm.Epoch)));
        var adapter = Build(new FakeDirectory(Vm.Running()), new FakeControl(), reader);

        var listed = Assert.Single(await adapter.ListRunningAsync(TestContext.Current.CancellationToken));

        Assert.True(listed.Activity.HasRunningExec);
        Assert.False(listed.Activity.IsUnknown);
    }

    [Fact]
    public async Task Only_running_vms_with_a_backend_ref_are_listed()
    {
        var running = Vm.Running();
        var directory = new FakeDirectory(
            running,
            Vm.Running(backendRef: null),
            running with { VmId = Guid.CreateVersion7(), State = VmRunState.Saved });
        var adapter = Build(directory, new FakeControl(), IdleReader());

        Assert.Equal([running.VmId], (await adapter.ListRunningAsync(TestContext.Current.CancellationToken)).Select(r => r.Vm.VmId));
    }

    [Fact]
    public async Task A_missing_directory_a_failed_read_and_a_slow_read_propagate_so_the_governor_admits_untracked()
    {
        var none = Build(null, new FakeControl(), IdleReader());
        var failing = Build(new FakeDirectory { Failure = new InvalidOperationException("db down") }, new FakeControl(), IdleReader());

        await Assert.ThrowsAsync<InvalidOperationException>(() => none.ListRunningAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => failing.ListRunningAsync(TestContext.Current.CancellationToken));

        var slow = Build(new SlowDirectory(), new FakeControl(), IdleReader());
        var task = slow.ListRunningAsync(TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromSeconds(6));
        await Assert.ThrowsAsync<TimeoutException>(() => task);
    }

    [Fact]
    public async Task Cancellation_propagates_from_the_directory_and_from_the_activity_reader()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var adapter = Build(new FakeDirectory(Vm.Running()), new FakeControl(), IdleReader());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.ListRunningAsync(cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.SaveAsync(Guid.NewGuid(), cts.Token));

        using var late = new CancellationTokenSource();
        var reader = new FakeReader(_ =>
        {
            late.Cancel();
            return Task.FromCanceled<VmActivity?>(late.Token);
        });
        var withReader = Build(new FakeDirectory(Vm.Running()), new FakeControl(), reader);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => withReader.ListRunningAsync(late.Token));
    }

    [Fact]
    public async Task Save_goes_through_the_control_port_for_an_idle_saveable_vm()
    {
        var info = Vm.Running();
        var control = new FakeControl();
        var adapter = Build(new FakeDirectory(info), control, IdleReader());

        await adapter.SaveAsync(info.VmId, TestContext.Current.CancellationToken);

        Assert.Equal([info.VmId], control.Saved);
    }

    [Fact]
    public async Task Save_is_refused_without_calling_the_control_port_when_the_vm_is_busy_unknown_unsaveable_or_gone()
    {
        var busy = Vm.Running();
        var unknown = Vm.Running();
        var cannot = Vm.Running(canSave: false);
        var control = new FakeControl();
        var reader = new FakeReader(vm => Task.FromResult<VmActivity?>(
            vm.VmId == busy.VmId ? new VmActivity(false, true, false, Vm.Epoch)
            : vm.VmId == unknown.VmId ? null
            : Vm.Idle()));
        var adapter = Build(new FakeDirectory(busy, unknown, cannot), control, reader);

        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.SaveAsync(busy.VmId, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.SaveAsync(unknown.VmId, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.SaveAsync(cannot.VmId, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.SaveAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));

        Assert.Empty(control.Saved);
    }

    [Fact]
    public async Task Save_without_a_control_port_fails_closed()
    {
        var info = Vm.Running();
        var adapter = Build(new FakeDirectory(info), null, IdleReader());

        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.SaveAsync(info.VmId, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(VmControlOutcome.UnknownVm)]
    [InlineData(VmControlOutcome.NotAllowedInState)]
    [InlineData(VmControlOutcome.StateChanged)]
    public async Task A_save_the_vm_module_did_not_apply_is_reported_as_a_failure(VmControlOutcome outcome)
    {
        var info = Vm.Running();
        var adapter = Build(new FakeDirectory(info), new FakeControl { Outcome = outcome }, IdleReader());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.SaveAsync(info.VmId, TestContext.Current.CancellationToken));

        Assert.Contains(outcome.ToString(), ex.Message, StringComparison.Ordinal);
    }

    private sealed class SlowDirectory : IAgentVmDirectory
    {
        public Task<IReadOnlyList<AgentVmInfo>> ListForAgentAsync(Guid agentId, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<AgentVmInfo>> ListRunningAsync(CancellationToken ct) => new TaskCompletionSource<IReadOnlyList<AgentVmInfo>>().Task;
    }
}
