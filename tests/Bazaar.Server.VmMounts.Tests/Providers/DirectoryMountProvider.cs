using System.Collections.Concurrent;
using Bazaar.Server.VmMounts.Abstractions;

namespace Bazaar.Server.VmMounts.Tests.Providers;

/// <summary>
/// A provider that "mounts" with plain directories, for tests and for a developer machine with no hypervisor. Attaching creates a
/// symbolic link <c>ViewRoot/vmId/mountId</c> to the host directory, which stands for what the guest would see; detaching removes
/// the link and never touches the target. It enforces no read-only mode and cannot observe reads, so canary reads are raised by
/// the test through <see cref="RaiseCanaryRead"/>.
/// </summary>
public sealed class DirectoryMountProvider(string viewRoot) : IMountProvider
{
    private readonly ConcurrentDictionary<Guid, Func<CanaryAccess, ValueTask>> _watchers = new();

    /// <summary>The link a guest would see for a mount, whether or not it is attached now.</summary>
    public string ViewPath(Guid vmId, Guid mountId) => Path.Combine(viewRoot, vmId.ToString("N"), mountId.ToString("N"));

    public bool IsAttached(Guid vmId, Guid mountId) => Directory.Exists(ViewPath(vmId, mountId));

    public Task AttachAsync(MountProviderRequest request, CancellationToken ct)
    {
        var view = ViewPath(request.VmId, request.MountId);
        Directory.CreateDirectory(Path.GetDirectoryName(view)!);
        if (!Directory.Exists(request.HostPath))
            throw new DirectoryNotFoundException("The host directory to mount does not exist.");
        Directory.CreateSymbolicLink(view, request.HostPath);
        return Task.CompletedTask;
    }

    public Task DetachAsync(MountProviderRequest request, CancellationToken ct)
    {
        var view = ViewPath(request.VmId, request.MountId);
        var info = new DirectoryInfo(view);
        // Delete the link only. Directory.Delete on a link removes the link, not the target.
        if (info.LinkTarget is not null)
            info.Delete();
        return Task.CompletedTask;
    }

    public Task<IAsyncDisposable> WatchCanariesAsync(CanaryWatch watch, Func<CanaryAccess, ValueTask> onAccess, CancellationToken ct)
    {
        _watchers[watch.MountId] = onAccess;
        return Task.FromResult<IAsyncDisposable>(new Watch(() => _watchers.TryRemove(watch.MountId, out _)));
    }

    /// <summary>Pretends the guest read a canary. Returns false when no watch is active for the mount.</summary>
    public async Task<bool> RaiseCanaryRead(Guid mountId, string relativePath, DateTimeOffset at)
    {
        if (!_watchers.TryGetValue(mountId, out var callback))
            return false;
        await callback(new CanaryAccess(mountId, relativePath, at)).ConfigureAwait(false);
        return true;
    }

    private sealed class Watch(Action stop) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            stop();
            return ValueTask.CompletedTask;
        }
    }
}
