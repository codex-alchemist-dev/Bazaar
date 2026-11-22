using Bazaar.Server.Vms.Abstractions;
using Bazaar.Server.Vms.Environments;
using Bazaar.Server.Vms.Lifecycle;
using Bazaar.Server.Vms.Ports;

namespace Bazaar.Server.Vms.Tests.Ports;

public sealed class EnvironmentAgentVmDirectoryTests
{
    private static readonly DateTimeOffset _epoch = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private static EnvironmentVmRecord Record(Guid agent, VmState state, string? backendRef = "vm", VmOs os = VmOs.Linux) =>
        new(Guid.CreateVersion7(), agent, os, Guid.CreateVersion7(), backendRef, state, 2, 2048, 30, _epoch, _epoch);

    [Fact]
    public async Task List_for_agent_skips_retired_and_other_agents_and_keeps_order()
    {
        var agent = Guid.CreateVersion7();
        var first = Record(agent, VmState.Running, "a", VmOs.Linux);
        var retired = Record(agent, VmState.Retired);
        var second = Record(agent, VmState.Saved, "b", VmOs.Windows);
        var someoneElse = Record(Guid.CreateVersion7(), VmState.Running);
        var directory = new EnvironmentAgentVmDirectory(new FakeReader(first, retired, second, someoneElse));

        var list = await directory.ListForAgentAsync(agent, TestContext.Current.CancellationToken);

        Assert.Equal([first.Id, second.Id], list.Select(v => v.VmId));
        Assert.All(list, v => Assert.Equal(agent, v.AgentId));
        Assert.Equal([VmRunState.Running, VmRunState.Saved], list.Select(v => v.State));
        Assert.Equal([VmGuestOs.Linux, VmGuestOs.Windows], list.Select(v => v.Os));
    }

    [Fact]
    public async Task List_for_an_agent_with_no_vms_is_empty()
    {
        var directory = new EnvironmentAgentVmDirectory(new FakeReader(Record(Guid.CreateVersion7(), VmState.Running)));

        Assert.Empty(await directory.ListForAgentAsync(Guid.CreateVersion7(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task List_running_returns_running_vms_of_every_agent_each_with_a_backend_ref()
    {
        var one = Record(Guid.CreateVersion7(), VmState.Running, "r1");
        var two = Record(Guid.CreateVersion7(), VmState.Running, "r2");
        var brokenRow = Record(Guid.CreateVersion7(), VmState.Running, null);
        var directory = new EnvironmentAgentVmDirectory(new FakeReader(one, two, brokenRow, Record(Guid.CreateVersion7(), VmState.Saved)));

        var list = await directory.ListRunningAsync(TestContext.Current.CancellationToken);

        Assert.Equal([one.Id, two.Id], list.Select(v => v.VmId));
        Assert.All(list, v =>
        {
            Assert.Equal(VmRunState.Running, v.State);
            Assert.NotNull(v.BackendRef);
        });
    }

    [Fact]
    public async Task A_failing_read_propagates_and_cancellation_reaches_the_reader()
    {
        var failing = new EnvironmentAgentVmDirectory(new FakeReader { Failure = new InvalidOperationException("db down") });
        await Assert.ThrowsAsync<InvalidOperationException>(() => failing.ListRunningAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => failing.ListForAgentAsync(Guid.CreateVersion7(), TestContext.Current.CancellationToken));

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var directory = new EnvironmentAgentVmDirectory(new FakeReader());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => directory.ListRunningAsync(cts.Token));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_backends_save_capability_and_the_rows_memory_are_reported_for_both_lists(bool supportsSave)
    {
        var agent = Guid.CreateVersion7();
        var row = Record(agent, VmState.Running, "r1");
        var directory = new EnvironmentAgentVmDirectory(new FakeReader(row), new CapabilityBackend(supportsSave));

        var running = Assert.Single(await directory.ListRunningAsync(TestContext.Current.CancellationToken));
        var forAgent = Assert.Single(await directory.ListForAgentAsync(agent, TestContext.Current.CancellationToken));

        Assert.All(new[] { running, forAgent }, v =>
        {
            Assert.Equal(supportsSave, v.CanSaveState);
            Assert.Equal(2048, v.MemoryMb);
        });
    }

    [Fact]
    public async Task Without_a_backend_the_save_capability_is_unknown_not_false_or_true()
    {
        var directory = new EnvironmentAgentVmDirectory(new FakeReader(Record(Guid.CreateVersion7(), VmState.Running)));

        var running = Assert.Single(await directory.ListRunningAsync(TestContext.Current.CancellationToken));

        Assert.Null(running.CanSaveState);
    }

    private sealed class CapabilityBackend(bool supportsSave) : IVmBackend
    {
        public bool SupportsSaveState { get; } = supportsSave;

        public Task<VmHandle> CreateChildAsync(ImageVersionId parent, VmSpec spec, CancellationToken ct) => throw new NotSupportedException();

        public Task StartAsync(VmHandle vm, CancellationToken ct) => throw new NotSupportedException();

        public Task StopAsync(VmHandle vm, StopMode mode, CancellationToken ct) => throw new NotSupportedException();

        public Task SaveStateAsync(VmHandle vm, CancellationToken ct) => throw new NotSupportedException();

        public Task<CheckpointId> CheckpointAsync(VmHandle vm, CancellationToken ct) => throw new NotSupportedException();

        public Task RestoreAsync(VmHandle vm, CheckpointId id, CancellationToken ct) => throw new NotSupportedException();

        public Task RetireAsync(VmHandle vm, CancellationToken ct) => throw new NotSupportedException();

        public Task<VmState> GetStateAsync(VmHandle vm, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class FakeReader(params EnvironmentVmRecord[] rows) : IEnvironmentVmReader
    {
        public Exception? Failure { get; init; }

        public Task<IReadOnlyList<EnvironmentVmRecord>> ListVmsForAgentAsync(Guid agentId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Failure is null
                ? Task.FromResult<IReadOnlyList<EnvironmentVmRecord>>(rows.Where(r => r.AgentId == agentId).ToList())
                : Task.FromException<IReadOnlyList<EnvironmentVmRecord>>(Failure);
        }

        public Task<IReadOnlyList<EnvironmentVmRecord>> ListRunningVmsAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Failure is null
                ? Task.FromResult<IReadOnlyList<EnvironmentVmRecord>>(rows.Where(r => r.State == VmState.Running).ToList())
                : Task.FromException<IReadOnlyList<EnvironmentVmRecord>>(Failure);
        }
    }
}
