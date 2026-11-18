using System.Collections.Concurrent;
using Bazaar.Server.VmMounts.Abstractions;

namespace Bazaar.Server.VmMounts.Tests.Providers;

/// <summary>
/// An in-memory <see cref="IMountProvider"/> for tests: it records every call, can be told to fail, can hold a call open, and can
/// raise a canary read. It exposes nothing.
/// </summary>
public sealed class FakeMountProvider : IMountProvider
{
    private readonly ConcurrentQueue<string> _calls = new();
    private readonly ConcurrentDictionary<Guid, MountProviderRequest> _attached = new();
    private readonly ConcurrentDictionary<Guid, Func<CanaryAccess, ValueTask>> _watchers = new();

    /// <summary>Every call in order, as <c>attach:id</c>, <c>detach:id</c> or <c>watch:id</c>.</summary>
    public IReadOnlyList<string> Calls => [.. _calls];

    public IReadOnlyCollection<MountProviderRequest> Attached => [.. _attached.Values];

    /// <summary>When set, the next attach throws this exception once.</summary>
    public Exception? FailNextAttach { get; set; }

    /// <summary>When set, the next detach throws this exception once.</summary>
    public Exception? FailNextDetach { get; set; }

    /// <summary>Every detach request, in order.</summary>
    public IReadOnlyList<MountProviderRequest> DetachedRequests => [.. _detached];

    private readonly ConcurrentQueue<MountProviderRequest> _detached = new();

    /// <summary>When set, every detach throws this exception until it is cleared.</summary>
    public Exception? FailDetach { get; set; }

    /// <summary>When set, attach waits for this task before it returns. Lets a test hold one VM's operation open.</summary>
    public Task? AttachGate { get; set; }

    /// <summary>Called when an attach starts, for tests that count overlapping calls.</summary>
    public Action? OnAttachStarted { get; set; }

    public async Task AttachAsync(MountProviderRequest request, CancellationToken ct)
    {
        _calls.Enqueue($"attach:{request.MountId}");
        OnAttachStarted?.Invoke();
        if (AttachGate is { } gate)
            await gate.WaitAsync(ct).ConfigureAwait(false);
        if (FailNextAttach is { } next)
        {
            FailNextAttach = null;
            throw next;
        }

        _attached[request.MountId] = request;
    }

    public Task DetachAsync(MountProviderRequest request, CancellationToken ct)
    {
        _calls.Enqueue($"detach:{request.MountId}");
        _detached.Enqueue(request);
        if (FailNextDetach is { } once)
        {
            FailNextDetach = null;
            throw once;
        }

        if (FailDetach is { } failure)
            throw failure;
        _attached.TryRemove(request.MountId, out _);
        return Task.CompletedTask;
    }

    public Task<IAsyncDisposable> WatchCanariesAsync(CanaryWatch watch, Func<CanaryAccess, ValueTask> onAccess, CancellationToken ct)
    {
        _calls.Enqueue($"watch:{watch.MountId}");
        _watchers[watch.MountId] = onAccess;
        return Task.FromResult<IAsyncDisposable>(new Watch(() => _watchers.TryRemove(watch.MountId, out _)));
    }

    /// <summary>Pretends the guest read a canary file. Returns false when no watch is active for the mount.</summary>
    public async Task<bool> RaiseCanaryReadAsync(Guid mountId, string relativePath, DateTimeOffset at)
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
