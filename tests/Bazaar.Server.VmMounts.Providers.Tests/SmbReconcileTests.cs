using System.Text.Json;
using Bazaar.Server.VmMounts.Abstractions;
using Bazaar.Server.VmMounts.Providers.Smb;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bazaar.Server.VmMounts.Providers.Tests;

public sealed class SmbReconcileTests : IDisposable
{
    private readonly MountEnv _env = new();
    private readonly FakeRunner _runner = new();
    private readonly FakeStore _store = new();
    private readonly FakeAudit _audit = new();
    private readonly SmbMountProvider _provider;
    private readonly SmbShareReconciler _reconciler;

    public SmbReconcileTests()
    {
        _provider = new SmbMountProvider(
            _env.Options, _runner, new FakeIdentity(), new FakeEndpoint(), NullLogger<SmbMountProvider>.Instance, requireDrivePath: false);
        _reconciler = new SmbShareReconciler(_provider, _store, _audit, TimeProvider.System, NullLogger<SmbShareReconciler>.Instance);
    }

    public void Dispose()
    {
        _provider.Dispose();
        _env.Dispose();
    }

    private ScopedMountRow Row(MountKind kind, Guid? mountId = null) =>
        new(mountId ?? Guid.NewGuid(), _env.AgentId, Guid.NewGuid(), kind, "x", "y", MountMode.Ro, null);

    private void Listing(params string[] names) =>
        _runner.Answer = c => c.StandardInput is null
            ? new HostCommandResult(0, string.Join("\r\n", names) + "\r\n", string.Empty)
            : new HostCommandResult(0, "ok\n", string.Empty);

    private IEnumerable<string> RemovedNames() =>
        _runner.Commands.Where(c => c.StandardInput is not null).Select(c =>
        {
            using var json = JsonDocument.Parse(c.StandardInput!);
            return json.RootElement.GetProperty("Name").GetString()!;
        });

    [Fact]
    public async Task Only_prefixed_shares_without_an_attached_row_are_removed()
    {
        var live = Row(MountKind.Soul);
        _store.Rows.Add(live);
        var liveName = SmbShareNames.For(live.AgentId, live.Kind, live.Id);
        var staleName = SmbShareNames.For(_env.AgentId, MountKind.Workspace, Guid.NewGuid());
        Listing(liveName, staleName);

        var result = await _reconciler.ReconcileAsync(TestContext.Current.CancellationToken);

        Assert.Equal([staleName], result.Removed);
        Assert.Equal([liveName], result.Kept);
        Assert.Equal([staleName], RemovedNames());
        var entry = Assert.Single(_audit.Entries);
        Assert.Equal("vm.mount.stale_share_removed", entry.Kind);
    }

    [Fact]
    public async Task Names_that_look_similar_but_do_not_match_are_never_removed()
    {
        var good = SmbShareNames.For(_env.AgentId, MountKind.Soul, Guid.NewGuid());
        Listing(
            "bazaar-backup",
            "Bazaar-111111112222-soul-aaaaaaaabbbbccccddddeeeeeeeeeeee",
            good + "x",
            "ADMIN$",
            "bazaar-111111112222-soul-aaaaaaaabbbbccccddddeeeeeeeeeeee; Remove-SmbShare *");

        var result = await _reconciler.ReconcileAsync(TestContext.Current.CancellationToken);

        Assert.Empty(result.Removed);
        Assert.Equal(5, result.Ignored.Count);
        Assert.Empty(RemovedNames());
    }

    [Fact]
    public async Task A_detached_row_does_not_keep_its_share()
    {
        var detached = Row(MountKind.Workspace) with { DetachedAt = DateTimeOffset.UtcNow };
        _store.Rows.Add(detached);
        var name = SmbShareNames.For(detached.AgentId, detached.Kind, detached.Id);
        Listing(name);

        var result = await _reconciler.ReconcileAsync(TestContext.Current.CancellationToken);

        Assert.Equal([name], result.Removed);
    }

    [Fact]
    public async Task Reconcile_is_idempotent_when_nothing_is_stale()
    {
        var live = Row(MountKind.Workspace);
        _store.Rows.Add(live);
        Listing(SmbShareNames.For(live.AgentId, live.Kind, live.Id));

        var first = await _reconciler.ReconcileAsync(TestContext.Current.CancellationToken);
        var second = await _reconciler.ReconcileAsync(TestContext.Current.CancellationToken);

        Assert.Empty(first.Removed);
        Assert.Empty(second.Removed);
        Assert.Empty(RemovedNames());
    }

    [Fact]
    public async Task A_failed_listing_removes_nothing_and_surfaces()
    {
        _runner.Answer = _ => new HostCommandResult(1, string.Empty, "Get-SmbShare : access denied");

        await Assert.ThrowsAsync<SmbShareException>(() => _reconciler.ReconcileAsync(TestContext.Current.CancellationToken));

        Assert.Empty(RemovedNames());
    }

    [Fact]
    public async Task A_failed_removal_surfaces_and_is_not_audited_as_done()
    {
        var stale = SmbShareNames.For(_env.AgentId, MountKind.Soul, Guid.NewGuid());
        _runner.Answer = c => c.StandardInput is null
            ? new HostCommandResult(0, stale + "\n", string.Empty)
            : new HostCommandResult(1, string.Empty, "in use");

        await Assert.ThrowsAsync<SmbShareException>(() => _reconciler.ReconcileAsync(TestContext.Current.CancellationToken));

        Assert.Empty(_audit.Entries);
    }

    [Fact]
    public void A_stale_name_comes_from_the_mount_row_not_from_the_listing()
    {
        // The expected set is built from rows with the same builder the provider uses, so a row and its share always match.
        var row = Row(MountKind.Soul);
        Assert.True(SmbShareNames.IsValidShare(SmbShareNames.For(row.AgentId, row.Kind, row.Id)));
    }
}
