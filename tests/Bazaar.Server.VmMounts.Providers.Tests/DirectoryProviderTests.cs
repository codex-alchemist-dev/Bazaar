using Bazaar.Server.VmMounts.Abstractions;
using Bazaar.Server.VmMounts.Providers.Local;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bazaar.Server.VmMounts.Providers.Tests;

public sealed class DirectoryProviderTests : IDisposable
{
    private readonly MountEnv _env = new();
    private readonly DirectoryMountProvider _provider;

    public DirectoryProviderTests() =>
        _provider = new DirectoryMountProvider(_env.Options, NullLogger<DirectoryMountProvider>.Instance);

    public void Dispose() => _env.Dispose();

    [Fact]
    public async Task Attach_records_the_canonical_mount_and_exposes_nothing()
    {
        var request = _env.Request(MountKind.Workspace, MountMode.Rw);

        await _provider.AttachAsync(request, TestContext.Current.CancellationToken);

        var record = Assert.Single(_provider.Attached);
        Assert.Equal(_env.WorkspacePath, record.HostPath);
        Assert.Equal(MountMode.Rw, record.Mode);
        // No link, bind or marker file appears anywhere under the agents root.
        Assert.Empty(Directory.EnumerateFileSystemEntries(_env.WorkspacePath));
        Assert.All(Directory.EnumerateFileSystemEntries(_env.Root, "*", SearchOption.AllDirectories), p => Assert.Null(new FileInfo(p).LinkTarget));
    }

    [Fact]
    public async Task Attach_is_idempotent_for_an_identical_request()
    {
        var request = _env.Request(MountKind.Soul, MountMode.Ro);

        await _provider.AttachAsync(request, TestContext.Current.CancellationToken);
        await _provider.AttachAsync(request, TestContext.Current.CancellationToken);

        Assert.Single(_provider.Attached);
    }

    [Fact]
    public async Task A_repeat_attach_with_other_settings_is_refused_and_keeps_the_first()
    {
        var request = _env.Request(MountKind.Workspace, MountMode.Ro);
        await _provider.AttachAsync(request, TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<MountRefusedException>(
            () => _provider.AttachAsync(request with { Mode = MountMode.Rw }, TestContext.Current.CancellationToken));

        Assert.Equal(MountRefusalReason.AlreadyAttached, ex.Reason);
        Assert.Equal(MountMode.Ro, Assert.Single(_provider.Attached).Mode);
    }

    [Fact]
    public async Task A_read_write_soul_mount_is_refused()
    {
        var ex = await Assert.ThrowsAsync<MountRefusedException>(
            () => _provider.AttachAsync(_env.Request(MountKind.Soul, MountMode.Rw), TestContext.Current.CancellationToken));

        Assert.Equal(MountRefusalReason.ModeNotAllowed, ex.Reason);
        Assert.Empty(_provider.Attached);
    }

    [Fact]
    public async Task Paths_outside_the_agents_root_and_linked_paths_are_refused()
    {
        var outside = Path.Combine(Path.GetTempPath(), "bazaar-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            await Assert.ThrowsAsync<MountRefusedException>(
                () => _provider.AttachAsync(_env.Request(MountKind.Workspace, MountMode.Rw, outside), TestContext.Current.CancellationToken));

            var link = Path.Combine(_env.WorkspacePath, "link");
            Directory.CreateSymbolicLink(link, outside);
            var ex = await Assert.ThrowsAsync<MountRefusedException>(
                () => _provider.AttachAsync(_env.Request(MountKind.Workspace, MountMode.Rw, link), TestContext.Current.CancellationToken));
            Assert.Equal(MountRefusalReason.LinkNotAllowed, ex.Reason);
            Assert.Empty(_provider.Attached);
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public async Task Detach_is_idempotent_and_never_touches_the_host_directory()
    {
        var marker = Path.Combine(_env.WorkspacePath, "keep.txt");
        await File.WriteAllTextAsync(marker, "data", TestContext.Current.CancellationToken);
        var request = _env.Request(MountKind.Workspace, MountMode.Rw);
        await _provider.AttachAsync(request, TestContext.Current.CancellationToken);

        await _provider.DetachAsync(request, TestContext.Current.CancellationToken);
        await _provider.DetachAsync(request, TestContext.Current.CancellationToken);

        Assert.Empty(_provider.Attached);
        Assert.True(File.Exists(marker));
    }

    [Fact]
    public async Task The_canary_watch_is_a_disposable_no_op()
    {
        await using var watch = await _provider.WatchCanariesAsync(
            new CanaryWatch(Guid.NewGuid(), [".canary/a"]), _ => ValueTask.CompletedTask, TestContext.Current.CancellationToken);

        Assert.NotNull(watch);
    }
}
