using Bazaar.Server.VmMounts.Abstractions;
using Bazaar.Server.VmMounts.Stores;
using Bazaar.Server.VmMounts.Tests.Providers;
using Bazaar.Server.Vms.Environments;
using Bazaar.Testing.Postgres;
using Npgsql;
using MountKind = Bazaar.Server.VmMounts.Abstractions.MountKind;
using MountMode = Bazaar.Server.VmMounts.Abstractions.MountMode;

namespace Bazaar.Server.VmMounts.Tests;

/// <summary>A cloned database with the <c>vms</c> migrations and this module's own <c>vm_mounts</c> migration applied.</summary>
public class MountsDb : PgFixture, IAsyncLifetime
{
    public static readonly DateTimeOffset Epoch = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    private int _version = 1;

    public EnvironmentRepository Repository => new(DataSource);

    public PostgresScopedMountStore Store => new(DataSource);

    public PostgresCanaryStore CanaryStore => new(DataSource);

    public override async ValueTask InitializeAsync()
    {
        if (PgSource.IsUnsupportedHost)
            return;
        await base.InitializeAsync();
        // The vms tables first (scoped_mount is theirs), then this module's table, which has a foreign key to it.
        await ApplyAsync(typeof(EnvironmentRepository).Assembly, ".components.vms.");
        await ApplyAsync(typeof(PostgresCanaryStore).Assembly, ".components.vm_mounts.");
    }

    private async Task ApplyAsync(System.Reflection.Assembly assembly, string marker)
    {
        var names = assembly.GetManifestResourceNames()
            .Where(n => n.EndsWith(".sql", StringComparison.Ordinal) && n.Contains(marker, StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        Assert.NotEmpty(names);
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
    public async Task<Guid> InsertVmAsync(Guid agentId, VmOs os = VmOs.Linux, VmState state = VmState.Running)
    {
        var image = new GoldenImageRecord(
            Guid.CreateVersion7(), os, Interlocked.Increment(ref _version), new string('a', 64), "1.0.0", GoldenImageState.Ready, Epoch);
        await Repository.InsertGoldenImageAsync(image, TestContext.Current.CancellationToken);
        var vm = new EnvironmentVmRecord(Guid.CreateVersion7(), agentId, os, image.Id, null, state, 2, 2048, 30, Epoch, Epoch);
        await Repository.InsertVmAsync(vm, TestContext.Current.CancellationToken);
        return vm.Id;
    }
}

public sealed class PostgresScopedMountStoreTests : MountsDb
{
    private static ScopedMountRow Row(
        Guid agent, Guid vm, string guest = "/mnt/bazaar/soul", MountKind kind = MountKind.Soul, MountMode mode = MountMode.Ro) =>
        new(Guid.CreateVersion7(), agent, vm, kind, "/data/agents/x/soul", guest, mode, null);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Insert_get_and_list_round_trip_kind_mode_and_paths()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var agent = Guid.CreateVersion7();
        var vm = await InsertVmAsync(agent);
        var soul = Row(agent, vm);
        var ws = Row(agent, vm, "/mnt/bazaar/workspace", MountKind.Workspace, MountMode.Rw);

        Assert.Equal(MountInsertResult.Inserted, await Store.TryInsertAsync(soul, GuestOs.Linux, Ct));
        Assert.Equal(MountInsertResult.Inserted, await Store.TryInsertAsync(ws, GuestOs.Linux, Ct));

        Assert.Equal(ws, await Store.GetAsync(ws.Id, Ct));
        var attached = await Store.ListAttachedForAgentAsync(agent, Ct);
        Assert.Equal(new[] { soul.Id, ws.Id }.Order(), attached.Select(r => r.Id));
        Assert.Contains(soul.Id, (await Store.ListAttachedAsync(Ct)).Select(r => r.Id));
        Assert.Null(await Store.GetAsync(Guid.CreateVersion7(), Ct));
        Assert.Equal(GuestOs.Linux, await Store.GetVmGuestOsAsync(vm, Ct));
        Assert.Null(await Store.GetVmGuestOsAsync(Guid.CreateVersion7(), Ct));
    }

    [Fact]
    public async Task A_second_attached_mount_on_the_same_vm_and_guest_path_is_refused_by_the_unique_index()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var agent = Guid.CreateVersion7();
        var otherAgent = Guid.CreateVersion7();
        var vm = await InsertVmAsync(agent);
        var otherVm = await InsertVmAsync(otherAgent);
        Assert.Equal(MountInsertResult.Inserted, await Store.TryInsertAsync(Row(agent, vm), GuestOs.Linux, Ct));

        Assert.Equal(MountInsertResult.GuestPathTaken, await Store.TryInsertAsync(Row(agent, vm), GuestOs.Linux, Ct));
        // Another VM may use the same guest path.
        Assert.Equal(MountInsertResult.Inserted, await Store.TryInsertAsync(Row(otherAgent, otherVm), GuestOs.Linux, Ct));
    }

    [Fact]
    public async Task A_vm_of_another_agent_a_retired_vm_a_missing_vm_or_another_os_is_not_eligible()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var agent = Guid.CreateVersion7();
        var vm = await InsertVmAsync(agent);
        var retired = await InsertVmAsync(Guid.CreateVersion7(), state: VmState.Retired);
        var retiredOwner = (await Repository.GetVmAsync(retired, Ct))!.AgentId;

        Assert.Equal(MountInsertResult.VmNotEligible, await Store.TryInsertAsync(Row(Guid.CreateVersion7(), vm), GuestOs.Linux, Ct));
        Assert.Equal(MountInsertResult.VmNotEligible, await Store.TryInsertAsync(Row(retiredOwner, retired), GuestOs.Linux, Ct));
        Assert.Equal(MountInsertResult.VmNotEligible, await Store.TryInsertAsync(Row(agent, Guid.CreateVersion7()), GuestOs.Linux, Ct));
        Assert.Equal(MountInsertResult.VmNotEligible, await Store.TryInsertAsync(Row(agent, vm), GuestOs.Windows, Ct));
        Assert.Empty(await Store.ListAttachedForAgentAsync(agent, Ct));
    }

    [Fact]
    public async Task Detaching_frees_the_guest_path_and_removes_the_mount_from_the_attached_list()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var agent = Guid.CreateVersion7();
        var vm = await InsertVmAsync(agent);
        var first = Row(agent, vm);
        await Store.TryInsertAsync(first, GuestOs.Linux, Ct);

        Assert.True(await Store.MarkDetachedAsync(first.Id, Epoch, Ct));
        Assert.False(await Store.MarkDetachedAsync(first.Id, Epoch, Ct));

        Assert.Empty(await Store.ListAttachedForAgentAsync(agent, Ct));
        Assert.Equal(Epoch, (await Store.GetAsync(first.Id, Ct))!.DetachedAt);
        Assert.Equal(MountInsertResult.Inserted, await Store.TryInsertAsync(Row(agent, vm), GuestOs.Linux, Ct));
    }

    [Fact]
    public async Task Canary_fingerprints_persist_are_found_after_retirement_and_are_unique()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var agent = Guid.CreateVersion7();
        var vm = await InsertVmAsync(agent);
        var mount = Row(agent, vm);
        await Store.TryInsertAsync(mount, GuestOs.Linux, Ct);
        var fingerprint = new string('b', 64);
        var canary = new CanaryRecord(Guid.CreateVersion7(), mount.Id, agent, vm, ".canary/credentials.json", fingerprint, Epoch, null);

        await CanaryStore.InsertAsync([canary], Ct);

        Assert.Equal(canary, await CanaryStore.FindByFingerprintAsync(fingerprint, Ct));
        Assert.Single(await CanaryStore.ListActiveForMountAsync(mount.Id, Ct));

        await CanaryStore.RetireForMountAsync(mount.Id, Epoch.AddMinutes(1), Ct);

        Assert.Empty(await CanaryStore.ListActiveForMountAsync(mount.Id, Ct));
        var retired = await CanaryStore.FindByFingerprintAsync(fingerprint, Ct);
        Assert.Equal(Epoch.AddMinutes(1), retired!.RetiredAt);
        Assert.Null(await CanaryStore.FindByFingerprintAsync(new string('c', 64), Ct));
        await Assert.ThrowsAsync<PostgresException>(
            () => CanaryStore.InsertAsync([canary with { Id = Guid.CreateVersion7() }], Ct));
    }

    [Fact]
    public async Task The_manager_runs_end_to_end_on_the_real_tables_with_the_directory_provider()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The directory provider links with symbolic links.");
        using var h = new MountHarness();
        var vm = await InsertVmAsync(h.Agent);
        var provider = new DirectoryMountProvider(Path.Combine(h.Root, "view"));
        var manager = new MountManager(
            Store, CanaryStore, provider, h.Transfer, h.Sink, h.Audit, h.Policy, h.Scrubber, h.LinkProbe, h.Options, h.Time);

        var soul = await manager.AttachAsync(h.SoulSpec(vm), Ct);
        Assert.True(provider.IsAttached(vm, soul.MountId));
        Assert.Equal(2, (await CanaryStore.ListActiveForMountAsync(soul.MountId, Ct)).Count);
        await Assert.ThrowsAsync<MountRefusedException>(() => manager.AttachAsync(h.SoulSpec(vm), Ct));
        // The VM row belongs to h.Agent, so another agent (with a valid folder of its own) cannot mount onto it.
        var stranger = Guid.CreateVersion7();
        h.CreateAgent(stranger);
        var wrongAgent = await Assert.ThrowsAsync<MountRefusedException>(
            () => manager.AttachAsync(h.WorkspaceSpec(vm, agent: stranger), Ct));
        Assert.Equal(MountRefusalReason.VmNotEligible, wrongAgent.Reason);

        var results = await manager.DetachAllForAgentAsync(h.Agent, DetachReason.KillSwitch, Ct);

        Assert.Single(results);
        Assert.False(provider.IsAttached(vm, soul.MountId));
        Assert.True(Directory.Exists(h.Soul(h.Agent)), "Detaching must never delete the host directory.");
        Assert.NotNull((await Store.GetAsync(soul.MountId, Ct))!.DetachedAt);
        Assert.Empty(await CanaryStore.ListActiveForMountAsync(soul.MountId, Ct));
    }
}
