using System.Collections.Concurrent;
using Bazaar.Server.VmMounts.Abstractions;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.VmMounts.Providers.Local;

/// <summary>A mount the directory provider holds, as it was validated at attach.</summary>
public sealed record DirectoryMountRecord(Guid MountId, Guid AgentId, Guid VmId, MountKind Kind, MountMode Mode, string HostPath, string GuestPath);

/// <summary>
/// The Linux and developer-machine <see cref="IMountProvider"/>. It never creates a link, a bind mount or an export: it validates each
/// request with the manager's own path guard (inside the agent's root, no link, no denied folder, Soul read-only) and records the mount in
/// memory. It exposes nothing to a guest. That is deliberate and safe (nothing is widened), and it is why it is the default off Windows:
/// the host composes and runs end to end, while the real Linux transport (virtiofs under QEMU/KVM, doc 16) stays a reported gap.
/// </summary>
/// <remarks>
/// Attach is idempotent for an identical request and refuses a repeat that names a different host path, mode or kind. Detach is idempotent,
/// needs no guest and never touches the host directory. It cannot observe canary reads, so the watch is a no-op (allowed by the port) and a
/// warning is logged.
/// </remarks>
public sealed partial class DirectoryMountProvider : IMountProvider
{
    private readonly MountRequestValidator _validator;
    private readonly ILogger<DirectoryMountProvider> _logger;
    private readonly ConcurrentDictionary<Guid, DirectoryMountRecord> _mounts = new();

    public DirectoryMountProvider(VmMountOptions options, ILogger<DirectoryMountProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _validator = new MountRequestValidator(options);
        _logger = logger;
    }

    /// <summary>The mounts held now, for tests and diagnostics.</summary>
    public IReadOnlyCollection<DirectoryMountRecord> Attached => [.. _mounts.Values];

    public bool IsAttached(Guid mountId) => _mounts.ContainsKey(mountId);

    public Task AttachAsync(MountProviderRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var hostPath = _validator.ValidateAttach(request);
        var record = new DirectoryMountRecord(request.MountId, request.AgentId, request.VmId, request.Kind, request.Mode, hostPath, request.GuestPath);

        var existing = _mounts.GetOrAdd(request.MountId, record);
        if (existing != record)
            throw new MountRefusedException(MountRefusalReason.AlreadyAttached, "The mount is already attached with different settings.");
        return Task.CompletedTask;
    }

    public Task DetachAsync(MountProviderRequest request, CancellationToken ct)
    {
        MountRequestValidator.ValidateIds(request);
        _mounts.TryRemove(request.MountId, out _);
        return Task.CompletedTask;
    }

    public Task<IAsyncDisposable> WatchCanariesAsync(CanaryWatch watch, Func<CanaryAccess, ValueTask> onAccess, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(watch);
        LogNoCanaryWatch(_logger, watch.MountId);
        return Task.FromResult<IAsyncDisposable>(NoopWatch.Instance);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The directory mount provider cannot observe canary reads; mount {MountId} has no read watch.")]
    private static partial void LogNoCanaryWatch(ILogger logger, Guid mountId);

    private sealed class NoopWatch : IAsyncDisposable
    {
        public static readonly NoopWatch Instance = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
