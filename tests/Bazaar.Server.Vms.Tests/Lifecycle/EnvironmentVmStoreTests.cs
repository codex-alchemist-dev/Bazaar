using Bazaar.Server.Vms.Environments;
using Bazaar.Server.Vms.Lifecycle;
using Bazaar.Server.Vms.Tests.Environments;
using Bazaar.Testing.Postgres;

namespace Bazaar.Server.Vms.Tests.Lifecycle;

/// <summary>The production store adapter against a real PostgreSQL.</summary>
public sealed class EnvironmentVmStoreTests(VmDb db) : IClassFixture<VmDb>
{
    private static readonly DateTimeOffset _later = VmDb.Epoch.AddMinutes(5);

    [Fact]
    public async Task Insert_then_compare_and_set_succeeds_on_the_current_state_and_fails_on_a_stale_one()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        var image = await db.InsertImageAsync();
        var store = new EnvironmentVmStore(db.Repository);
        var vm = new EnvironmentVmRecord(
            Guid.CreateVersion7(), Guid.CreateVersion7(), VmOs.Linux, image.Id, null, VmState.Stopped, 2, 2048, 30, VmDb.Epoch, VmDb.Epoch);

        await store.InsertAsync(vm, ct);
        Assert.Equal(vm, await store.GetAsync(vm.Id, ct));

        Assert.True(await store.TryUpdateStateAsync(vm.Id, VmState.Stopped, VmState.Running, _later, ct));
        Assert.False(await store.TryUpdateStateAsync(vm.Id, VmState.Stopped, VmState.Saved, _later, ct));

        var read = await store.GetAsync(vm.Id, ct);
        Assert.Equal(VmState.Running, read!.State);
        Assert.Equal(_later, read.UpdatedAt);

        Assert.True(await store.SetBackendRefAsync(vm.Id, "hyperv:abc", _later, ct));
        Assert.Equal("hyperv:abc", (await store.GetAsync(vm.Id, ct))!.BackendRef);
        Assert.Null(await store.GetAsync(Guid.CreateVersion7(), ct));
    }
}
