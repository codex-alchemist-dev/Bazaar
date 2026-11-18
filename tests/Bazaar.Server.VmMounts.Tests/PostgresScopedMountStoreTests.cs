using Bazaar.Server.VmMounts.Abstractions;
using Bazaar.Server.VmMounts.Providers;
using Bazaar.Server.VmMounts.Stores;
using Bazaar.Server.Vms.Environments;
using Bazaar.Testing.Postgres;
using Npgsql;
using MountKind = Bazaar.Server.VmMounts.Abstractions.MountKind;
using MountMode = Bazaar.Server.VmMounts.Abstractions.MountMode;

namespace Bazaar.Server.VmMounts.Tests;

/// <summary>A cloned database with the <c>vms</c> migrations applied. The mount table belongs to that component; this module adds none.</summary>
public class MountsDb : PgFixture, IAsyncLifetime
{
    public static readonly DateTimeOffset Epoch = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    private int _version = 1;

    public EnvironmentRepository Repository => new(DataSource);

    public PostgresScopedMountStore Store => new(DataSource);

    public override async ValueTask InitializeAsync()
    {
        if (PgSource.IsUnsupportedHost)
            return;
        await base.InitializeAsync();
        var assembly = typeof(EnvironmentRepository).Assembly;
        var names = assembly.GetManifestResourceNames()
            .Where(n => n.EndsWith(".sql", StringComparison.Ordinal) && n.Contains(".components.vms.", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal);
        foreach (var name in names)
        {
            await using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            await using var conn = await DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
            await using var cmd = new NpgsqlCommand(await reader.ReadToEndAsync(), conn);
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Inserts a golden image and a VM for an agent, and returns the VM id (scoped_mount.vm_id has a foreign key).</summary>
    public async Task<Guid> InsertVmAsync(Guid agentId)
    {
        var image = new GoldenImageRecord(
            Guid.CreateVersion7(), VmOs.Linux, Interlocked.Increment(ref _version), new string('a', 64), "1.0.0", GoldenImageState.Ready, Epoch);
        await Repository.InsertGoldenImageAsync(image, TestContext.Current.CancellationToken);
        var vm = new EnvironmentVmRecord(Guid.CreateVersion7(), agentId, VmOs.Linux, image.Id, null, VmState.Running, 2, 2048, 30, Epoch, Epoch);
        await Repository.InsertVmAsync(vm, TestContext.Current.CancellationToken);
        return vm.Id;
    }
}

public sealed class PostgresScopedMountStoreTests : MountsDb
{
    private static ScopedMountRow Row(
        Guid agent, Guid vm, string guest = "/mnt/bazaar/soul", MountKind kind = MountKind.Soul, MountMode mode = MountMode.Ro) =>
        new(Guid.CreateVersion7(), agent, vm, kind, "/data/agents/x/soul", guest, mode, null);

    [Fact]
    public async Task Insert_get_and_list_round_trip_kind_mode_and_paths()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var agent = Guid.CreateVersion7();
        var vm = await InsertVmAsync(agent);
        var soul = Row(agent, vm);
        var ws = Row(agent, vm, "/mnt/bazaar/workspace", MountKind.Workspace, MountMode.Rw);

        Assert.True(await Store.TryInsertAsync(soul, TestContext.Current.CancellationToken));
        Assert.True(await Store.TryInsertAsync(ws, TestContext.Current.CancellationToken));

        var read = await Store.GetAsync(ws.Id, TestContext.Current.CancellationToken);
        Assert.Equal(ws, read);
        var attached = await Store.ListAttachedForAgentAsync(agent, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { soul.Id, ws.Id }.Order(), attached.Select(r => r.Id));
        Assert.Null(await Store.GetAsync(Guid.CreateVersion7(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_second_attached_mount_on_the_same_vm_and_guest_path_is_refused_by_the_unique_index()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var agent = Guid.CreateVersion7();
        var vm = await InsertVmAsync(agent);
        var otherVm = await InsertVmAsync(Guid.CreateVersion7());
        Assert.True(await Store.TryInsertAsync(Row(agent, vm), TestContext.Current.CancellationToken));

        Assert.False(await Store.TryInsertAsync(Row(agent, vm), TestContext.Current.CancellationToken));
        // Another VM may use the same guest path.
        Assert.True(await Store.TryInsertAsync(Row(agent, otherVm), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Detaching_frees_the_guest_path_and_removes_the_mount_from_the_attached_list()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var agent = Guid.CreateVersion7();
        var vm = await InsertVmAsync(agent);
        var first = Row(agent, vm);
        await Store.TryInsertAsync(first, TestContext.Current.CancellationToken);

        Assert.True(await Store.MarkDetachedAsync(first.Id, Epoch, TestContext.Current.CancellationToken));
        Assert.False(await Store.MarkDetachedAsync(first.Id, Epoch, TestContext.Current.CancellationToken));

        Assert.Empty(await Store.ListAttachedForAgentAsync(agent, TestContext.Current.CancellationToken));
        Assert.Equal(Epoch, (await Store.GetAsync(first.Id, TestContext.Current.CancellationToken))!.DetachedAt);
        Assert.True(await Store.TryInsertAsync(Row(agent, vm), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_manager_runs_end_to_end_on_the_real_table_with_the_directory_provider()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The directory provider links with symbolic links.");
        using var h = new MountHarness();
        var vm = await InsertVmAsync(h.Agent);
        var provider = new DirectoryMountProvider(Path.Combine(h.Root, "view"));
        var manager = new MountManager(Store, provider, h.Transfer, h.Sink, h.Audit, h.Policy, h.Options, h.Time);

        var mount = await manager.AttachAsync(h.WorkspaceSpec(vm), TestContext.Current.CancellationToken);
        Assert.True(provider.IsAttached(vm, mount.MountId));
        await Assert.ThrowsAsync<MountRefusedException>(() => manager.AttachAsync(h.WorkspaceSpec(vm), TestContext.Current.CancellationToken));

        var results = await manager.DetachAllForAgentAsync(h.Agent, DetachReason.KillSwitch, TestContext.Current.CancellationToken);

        Assert.Single(results);
        Assert.False(provider.IsAttached(vm, mount.MountId));
        Assert.True(Directory.Exists(h.Workspace(h.Agent)), "Detaching must never delete the host directory.");
        Assert.NotNull((await Store.GetAsync(mount.MountId, TestContext.Current.CancellationToken))!.DetachedAt);
    }
}
