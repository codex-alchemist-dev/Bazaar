using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.VmMounts.Abstractions;
using Bazaar.Server.VmMounts.Canaries;
using Bazaar.Server.VmMounts.Containment;
using Bazaar.Server.VmMounts.Paths;

namespace Bazaar.Server.VmMounts;

/// <summary>
/// The scoped-mount manager (doc 16 WP8 "Scoped mounts", doc 15 WP8). The rules it enforces, in order of weight:
/// the host path jail and the mode floor (Soul read-only, workspace read-write) run before anything is exposed; a hard-taint or
/// kill-switch detach (a "containment" detach) never queues behind guest I/O, never calls the guest to read, never writes the profile
/// back, and asks <see cref="IGuestScrubber"/> to quarantine the guest; and every attach, detach and write-back leaves an audit row.
/// One lock per VM serialises all operations on that VM, and one lock per agent serialises everything that touches its profile.
/// </summary>
/// <remarks>
/// Lock order is always VM, then agent. Every guest call runs under <see cref="VmIoGate"/>: a server-side timeout, and a scope that a
/// containment detach cancels before it asks for the VM lock. An agent has at most one attached Soul mount (single profile writer).
/// </remarks>
public sealed class MountManager : IMountManager
{
    private readonly IScopedMountStore _store;
    private readonly ICanaryStore _canaryStore;
    private readonly IMountProvider _provider;
    private readonly IProfileTransfer _transfer;
    private readonly ICanarySink _sink;
    private readonly IAuditLog _audit;
    private readonly IMountContentPolicy _policy;
    private readonly IGuestScrubber _scrubber;
    private readonly IHardLinkProbe _linkProbe;
    private readonly IMountAttachGuard _attachGuard;
    private readonly VmMountOptions _options;
    private readonly TimeProvider _time;
    private readonly HostPathGuard _guard;
    private readonly VmLocks _vmLocks = new();
    private readonly VmLocks _agentLocks = new();
    private readonly CanaryRegistry _canaries = new();
    private readonly VmIoGate _io;

    /// <summary>Mounts that a containment detach has claimed. A flagged mount is never written back. Cleared only when the detach completes.</summary>
    private readonly ConcurrentDictionary<Guid, byte> _contained = new();

    public MountManager(
        IScopedMountStore store,
        ICanaryStore canaryStore,
        IMountProvider provider,
        IProfileTransfer transfer,
        ICanarySink sink,
        IAuditLog audit,
        IMountContentPolicy policy,
        IGuestScrubber scrubber,
        IHardLinkProbe linkProbe,
        IMountAttachGuard attachGuard,
        VmMountOptions options,
        TimeProvider? time = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _canaryStore = canaryStore ?? throw new ArgumentNullException(nameof(canaryStore));
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _transfer = transfer ?? throw new ArgumentNullException(nameof(transfer));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _scrubber = scrubber ?? throw new ArgumentNullException(nameof(scrubber));
        _linkProbe = linkProbe ?? throw new ArgumentNullException(nameof(linkProbe));
        _attachGuard = attachGuard ?? throw new ArgumentNullException(nameof(attachGuard));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _time = time ?? TimeProvider.System;
        _guard = new HostPathGuard(options);
        _io = new VmIoGate(options.GuestIoTimeout);
    }

    public async Task<AttachedMount> AttachAsync(MountSpec spec, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(spec);
        using var vmLease = await _vmLocks.AcquireAsync(spec.VmId, ct).ConfigureAwait(false);
        // The profile has one writer: a Soul attach holds the agent lock for its whole length.
        using var agentLease = spec.Kind == MountKind.Soul ? await _agentLocks.AcquireAsync(spec.AgentId, ct).ConfigureAwait(false) : null;

        GuardedPath guarded;
        string guestPath;
        try
        {
            EnsureModeMatchesKind(spec);
            guestPath = EnsureGuestPath(spec);
            // Before any scan, canary, provider or guest work: a veto from the browser module must cost nothing.
            if (spec.Kind == MountKind.Soul)
                await _attachGuard.CheckSoulAttachAsync(spec.VmId, ct).ConfigureAwait(false);
            guarded = _guard.Check(spec.AgentId, spec.Kind, spec.HostPath);
            MountTreeScanner.Scan(guarded.MountRoot, spec.Kind, _options.MaxScanEntries, _options.CaseInsensitive, _policy, _linkProbe);
            if (spec.Kind == MountKind.Soul && HostPathGuard.IsLink(ProfileDirectory(guarded.MountRoot)))
                throw new MountRefusedException(MountRefusalReason.LinkNotAllowed, "The profile folder is a link.");
            if (spec.Kind == MountKind.Soul)
            {
                var attached = await _store.ListAttachedForAgentAsync(spec.AgentId, ct).ConfigureAwait(false);
                if (attached.Any(r => r.Kind == MountKind.Soul && r.VmId != spec.VmId))
                    throw new MountRefusedException(
                        MountRefusalReason.VmNotEligible, "The agent already has a Soul mount on another VM; a profile has one writer.");
            }
        }
        catch (MountRefusedException ex)
        {
            await TryAuditRefusalAsync(spec, ex).ConfigureAwait(false);
            throw;
        }

        var mountId = Guid.CreateVersion7();
        var row = new ScopedMountRow(mountId, spec.AgentId, spec.VmId, spec.Kind, guarded.MountRoot, guestPath, spec.Mode, null);

        // The row goes in first: a crash after this point leaves a row that says "attached", the safe direction for a detach-all.
        var inserted = await _store.TryInsertAsync(row, spec.GuestOs, ct).ConfigureAwait(false);
        if (inserted != MountInsertResult.Inserted)
        {
            var ex = inserted == MountInsertResult.GuestPathTaken
                ? new MountRefusedException(MountRefusalReason.AlreadyAttached, "The guest path already carries an attached mount.")
                : new MountRefusedException(
                    MountRefusalReason.VmNotEligible, "The VM does not exist, belongs to another agent, runs another OS, or is retired.");
            await TryAuditRefusalAsync(spec, ex).ConfigureAwait(false);
            throw ex;
        }

        var request = ToRequest(row, spec.GuestOs);
        IReadOnlyList<CanaryRecord> planted = [];
        var profileFiles = 0;
        try
        {
            if (spec.Kind == MountKind.Soul)
            {
                // Canaries first, so the share is never visible without them.
                planted = CanaryRegistry.Plant(spec, mountId, guarded.MountRoot, _options.CanaryCount, _policy, _time.GetUtcNow());
                await _canaryStore.InsertAsync(planted, ct).ConfigureAwait(false);
            }

            await RunGuestIoAsync(spec.VmId, t => _provider.AttachAsync(request, t), ct).ConfigureAwait(false);

            if (spec.Kind == MountKind.Soul)
            {
                await ArmWatchAsync(mountId, [.. planted.Select(c => c.RelativePath)], ct).ConfigureAwait(false);

                var profileDirectory = ProfileDirectory(guarded.MountRoot);
                Directory.CreateDirectory(profileDirectory);
                var manifest = await _io.RunAsync(
                    spec.VmId,
                    t => _transfer.PushAsync(spec.VmId, spec.GuestOs, profileDirectory, LayoutOf(spec.GuestOs).ProfilePath, t),
                    ct).ConfigureAwait(false);
                profileFiles = manifest.Count;
            }

            await WriteAuditAsync(VmMountAuditActions.Attach, row, new JsonObject
            {
                ["canaries"] = planted.Count,
                ["profile_files"] = profileFiles,
            }, ct).ConfigureAwait(false);
        }
        catch
        {
            await RollBackAttachAsync(request, row).ConfigureAwait(false);
            throw;
        }

        return new AttachedMount(mountId, spec.AgentId, spec.VmId, spec.Kind, spec.Mode, row.HostPath, guestPath, planted.Count, profileFiles);
    }

    public async Task<DetachResult> DetachAsync(Guid mountId, DetachReason reason, CancellationToken ct)
    {
        var first = await _store.GetAsync(mountId, ct).ConfigureAwait(false)
            ?? throw new MountRefusedException(MountRefusalReason.NotAttached, "No such mount.");
        var containment = reason != DetachReason.Clean;
        if (containment)
            Contain(first);

        // A containment detach waits for the lock without the caller's token: abandoning it would leave the share exposed. The wait is
        // short because Contain cancelled the guest I/O that could hold the lock.
        using var lease = await _vmLocks.AcquireAsync(first.VmId, containment ? CancellationToken.None : ct).ConfigureAwait(false);

        // Re-read under the lock: a concurrent detach may have finished while this call waited.
        var row = await _store.GetAsync(mountId, CancellationToken.None).ConfigureAwait(false) ?? first;
        if (row.DetachedAt is not null)
        {
            _contained.TryRemove(mountId, out _);
            return new DetachResult(mountId, true, true, reason, WriteBackOutcome.NotAttempted);
        }

        var guestOs = await GuestOsOfAsync(row).ConfigureAwait(false);
        var writeBack = WriteBackOutcome.NotAttempted;
        if (reason == DetachReason.Clean && row.Kind == MountKind.Soul)
        {
            // While the share and the guest are still up. A rejected, failed or cancelled write-back never blocks the detach.
            writeBack = (await WriteBackCoreAsync(row, guestOs, ct).ConfigureAwait(false)).Outcome;
        }

        using var limit = new CancellationTokenSource(_options.ContainmentTimeout);
        // From here on the host side lets go whatever the caller's token says; a server-side limit keeps a hung provider from stalling.
        Exception? detachFailure = null;
        Exception? cleanupFailure = null;
        var profileDeleted = false;

        Exception? scrubFailure = null;
        var scrubbed = false;

        try
        {
            await _provider.DetachAsync(ToRequest(row, guestOs), limit.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            detachFailure = ex;
            await TryAuditAsync(VmMountAuditActions.DetachFailed, row, new JsonObject
            {
                ["reason"] = reason.ToString(),
                ["error"] = ex.GetType().Name,
            }).ConfigureAwait(false);
        }

        if (containment)
        {
            // Attempted even when the provider failed: pausing the guest also cuts it off.
            try
            {
                await _scrubber.ScrubAsync(new GuestScrubRequest(row.AgentId, row.VmId, row.Id, reason), limit.Token).ConfigureAwait(false);
                scrubbed = true;
            }
            catch (Exception ex)
            {
                scrubFailure = ex;
                await TryAuditAsync(VmMountAuditActions.ScrubFailed, row, new JsonObject
                {
                    ["reason"] = reason.ToString(),
                    ["error"] = ex.GetType().Name,
                }).ConfigureAwait(false);
            }
        }

        // Invariant: no cookie copy exists in the guest outside an attached Soul mount. Attempted after every Soul detach, last, so a
        // slow guest never delays the host side letting go; on containment the scrubber is the main defence and this still runs.
        using var cleanupLimit = new CancellationTokenSource(_options.ContainmentTimeout);
        if (row.Kind == MountKind.Soul)
        {
            try
            {
                await _transfer.DeleteGuestCopyAsync(row.VmId, LayoutOf(guestOs).ProfilePath, cleanupLimit.Token).ConfigureAwait(false);
                profileDeleted = true;
            }
            catch (Exception ex)
            {
                cleanupFailure = ex;
                await TryAuditAsync(VmMountAuditActions.GuestCleanupFailed, row, new JsonObject
                {
                    ["reason"] = reason.ToString(),
                    ["error"] = ex.GetType().Name,
                }).ConfigureAwait(false);
            }
        }

        if (detachFailure is null)
        {
            await _canaries.StopWatchAsync(mountId).ConfigureAwait(false);
            if (row.Kind == MountKind.Soul)
            {
                await _canaryStore.RetireForMountAsync(mountId, _time.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
                CanaryRegistry.RemoveFolder(row.HostPath);
            }

            await _store.MarkDetachedAsync(mountId, _time.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
            _contained.TryRemove(mountId, out _);
            await WriteAuditAsync(VmMountAuditActions.Detach, row, new JsonObject
            {
                ["reason"] = reason.ToString(),
                ["writeback"] = writeBack.ToString(),
                ["scrubbed"] = scrubbed,
                ["guest_profile_deleted"] = profileDeleted,
            }, CancellationToken.None).ConfigureAwait(false);
        }

        if (detachFailure is not null && scrubFailure is not null)
            throw new AggregateException("The mount could not be detached and the guest could not be scrubbed.", detachFailure, scrubFailure);
        if (detachFailure is not null)
            throw detachFailure;
        if (scrubFailure is not null)
            throw scrubFailure;
        // A failed cleanup is tolerated on containment only because the scrub succeeded (checked above); a clean detach reports it.
        if (cleanupFailure is not null && !containment)
            throw cleanupFailure;
        return new DetachResult(mountId, true, false, reason, writeBack, scrubbed, profileDeleted);
    }

    public async Task<IReadOnlyList<DetachResult>> DetachAllForAgentAsync(Guid agentId, DetachReason reason, CancellationToken ct)
    {
        var rows = await _store.ListAttachedForAgentAsync(agentId, ct).ConfigureAwait(false);
        if (reason != DetachReason.Clean)
        {
            // Claim every mount and cancel its VM's guest I/O before the first detach starts, so none of them queues behind another.
            foreach (var row in rows)
                Contain(row);
        }

        var results = new List<DetachResult>(rows.Count);
        List<Exception>? failures = null;
        foreach (var row in rows)
        {
            try
            {
                results.Add(await DetachAsync(row.Id, reason, ct).ConfigureAwait(false));
            }
            catch (Exception ex)
            {
                // Cancellation included: one mount that was cancelled or timed out must not leave the next one attached.
                (failures ??= []).Add(ex);
            }
        }

        if (failures is not null)
            throw new AggregateException($"{failures.Count} of {rows.Count} mounts of the agent could not be detached.", failures);
        return results;
    }

    public async Task<WriteBackResult> WriteBackProfileAsync(Guid mountId, CancellationToken ct)
    {
        var first = await _store.GetAsync(mountId, ct).ConfigureAwait(false);
        if (first is not { DetachedAt: null, Kind: MountKind.Soul } || _contained.ContainsKey(mountId))
            throw new MountRefusedException(MountRefusalReason.NotAttached, "The mount is not an attached, uncontained Soul mount.");

        using var lease = await _vmLocks.AcquireAsync(first.VmId, ct).ConfigureAwait(false);
        var row = await _store.GetAsync(mountId, ct).ConfigureAwait(false);
        if (row is not { DetachedAt: null } || _contained.ContainsKey(mountId))
            throw new MountRefusedException(MountRefusalReason.NotAttached, "The mount was detached or contained.");
        return await WriteBackCoreAsync(row, await GuestOsOfAsync(row).ConfigureAwait(false), ct).ConfigureAwait(false);
    }

    public async Task<bool> ReportCanaryTokenUseAsync(string tokenValue, string source, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(tokenValue);
        // A retired canary still matches: the value showing up after the detach is exactly what a canary is for.
        var canary = await _canaryStore.FindByFingerprintAsync(CanaryRegistry.Fingerprint(tokenValue), ct).ConfigureAwait(false);
        if (canary is null)
            return false;
        await ReportAsync(canary, CanaryUseKind.TokenSeen, SafeText.Clean(source), ct).ConfigureAwait(false);
        return true;
    }

    public async Task<int> RearmWatchesAsync(CancellationToken ct)
    {
        var armed = 0;
        foreach (var row in await _store.ListAttachedAsync(ct).ConfigureAwait(false))
        {
            if (row.Kind != MountKind.Soul)
                continue;
            var active = await _canaryStore.ListActiveForMountAsync(row.Id, ct).ConfigureAwait(false);
            if (active.Count == 0)
                continue;
            await ArmWatchAsync(row.Id, [.. active.Select(c => c.RelativePath)], ct).ConfigureAwait(false);
            armed++;
        }

        return armed;
    }

    /// <summary>Claims a mount for containment and cancels the guest I/O of its VM, so the detach is not queued behind it.</summary>
    private void Contain(ScopedMountRow row)
    {
        _contained[row.Id] = 0;
        _io.CancelAll(row.VmId);
    }

    private async Task ArmWatchAsync(Guid mountId, IReadOnlyList<string> paths, CancellationToken ct)
    {
        var watch = await _provider.WatchCanariesAsync(new CanaryWatch(mountId, paths), OnCanaryAccessAsync, ct).ConfigureAwait(false);
        await _canaries.SetWatchAsync(mountId, watch).ConfigureAwait(false);
    }

    private async ValueTask OnCanaryAccessAsync(CanaryAccess access)
    {
        var active = await _canaryStore.ListActiveForMountAsync(access.MountId, CancellationToken.None).ConfigureAwait(false);
        var canary = active.FirstOrDefault(c => c.RelativePath == access.RelativePath);
        if (canary is null)
            return;
        await ReportAsync(canary, CanaryUseKind.FileRead, "provider", CancellationToken.None, access.At).ConfigureAwait(false);
    }

    private async Task ReportAsync(
        CanaryRecord canary, CanaryUseKind kind, string source, CancellationToken ct, DateTimeOffset? at = null)
    {
        var when = at ?? _time.GetUtcNow();
        try
        {
            await _audit.AppendAsync(
                new AuditEntry(when, VmMountAuditActions.Actor, VmMountAuditActions.CanaryUse, new JsonObject
                {
                    ["mount_id"] = canary.MountId.ToString(),
                    ["agent_id"] = canary.AgentId.ToString(),
                    ["vm_id"] = canary.VmId.ToString(),
                    ["canary_id"] = canary.Fingerprint,
                    ["path"] = canary.RelativePath,
                    ["kind"] = kind.ToString(),
                    ["source"] = source,
                    ["retired"] = canary.RetiredAt is not null,
                }.ToJsonString()),
                ct).ConfigureAwait(false);
        }
        finally
        {
            // The sink pages the owner and freezes the agent. A failed audit write must not stop that.
            await _sink.ReportCanaryUseAsync(
                new CanaryUseReport(
                    canary.AgentId, canary.VmId, canary.MountId, canary.Fingerprint, canary.RelativePath, kind, source, when, canary.RetiredAt is not null),
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task<WriteBackResult> WriteBackCoreAsync(ScopedMountRow row, GuestOs guestOs, CancellationToken ct)
    {
        var profileDirectory = ProfileDirectory(row.HostPath);
        var staging = Path.Combine(_guard.AgentRoot(row.AgentId), _options.StagingFolderName, Guid.NewGuid().ToString("N"));
        try
        {
            // One writer per profile: the agent lock covers the pull and the swap.
            using var agentLease = await _agentLocks.AcquireAsync(row.AgentId, ct).ConfigureAwait(false);
            if (HostPathGuard.IsLink(profileDirectory))
                throw new ProfileTransferException(ProfileTransferFailure.Invalid, "The host profile folder is a link.");
            if (HostPathGuard.IsLink(Path.GetDirectoryName(staging)!))
                throw new ProfileTransferException(ProfileTransferFailure.Invalid, "The staging folder is a link.");

            var manifest = await _io.RunAsync(
                row.VmId, t => _transfer.PullAsync(row.VmId, guestOs, LayoutOf(guestOs).ProfilePath, staging, t), ct).ConfigureAwait(false);

            // No write-back after taint, even if the taint arrived while the profile was being read.
            if (_contained.ContainsKey(row.Id))
                throw new OperationCanceledException("The mount was contained during the write-back.");
            SwapIntoPlace(staging, profileDirectory);

            await WriteAuditAsync(VmMountAuditActions.WriteBack, row, new JsonObject
            {
                ["files"] = manifest.Count,
                ["bytes"] = manifest.TotalBytes,
                ["digest"] = manifest.Digest,
            }, CancellationToken.None).ConfigureAwait(false);
            return new WriteBackResult(row.Id, WriteBackOutcome.Written, manifest.Count, null);
        }
        catch (ProfileTransferException ex) when (ex.Failure != ProfileTransferFailure.Channel)
        {
            await TryAuditAsync(VmMountAuditActions.WriteBackRejected, row, new JsonObject
            {
                ["failure"] = ex.Failure.ToString(),
                ["detail"] = SafeText.Clean(ex.Message),
            }).ConfigureAwait(false);
            return new WriteBackResult(row.Id, WriteBackOutcome.Rejected, 0, SafeText.Clean(ex.Message));
        }
        catch (OperationCanceledException)
        {
            // Cancelled, timed out, or contained: the profile is untouched and the caller goes on to detach.
            await TryAuditAsync(VmMountAuditActions.WriteBackRejected, row, new JsonObject
            {
                ["failure"] = "Cancelled",
            }).ConfigureAwait(false);
            return new WriteBackResult(row.Id, WriteBackOutcome.Failed, 0, "The write-back was cancelled or timed out.");
        }
        catch (Exception ex) when (ex is not MountRefusedException)
        {
            await TryAuditAsync(VmMountAuditActions.WriteBackRejected, row, new JsonObject
            {
                ["failure"] = "Failed",
                ["detail"] = ex.GetType().Name,
            }).ConfigureAwait(false);
            return new WriteBackResult(row.Id, WriteBackOutcome.Failed, 0, SafeText.Clean(ex.Message));
        }
        finally
        {
            TryDeleteDirectory(staging);
        }
    }

    /// <summary>Replaces the host profile with the validated staging copy. The old profile is restored, and the backup removed, if the final move fails.</summary>
    private static void SwapIntoPlace(string staging, string profileDirectory)
    {
        var backup = staging + ".old";
        var hadOld = Directory.Exists(profileDirectory);
        if (hadOld)
            Directory.Move(profileDirectory, backup);
        try
        {
            if (!Directory.Exists(staging))
                Directory.CreateDirectory(staging);
            Directory.Move(staging, profileDirectory);
        }
        catch
        {
            if (hadOld && !Directory.Exists(profileDirectory))
                Directory.Move(backup, profileDirectory);
            TryDeleteDirectory(backup);
            throw;
        }

        TryDeleteDirectory(backup);
    }

    private async Task RollBackAttachAsync(MountProviderRequest request, ScopedMountRow row)
    {
        // Best effort, and never cancellable by the caller: an attach that failed halfway must not leave the share exposed.
        try
        {
            using var limit = new CancellationTokenSource(_options.ContainmentTimeout);
            await _provider.DetachAsync(request, limit.Token).ConfigureAwait(false);
            if (row.Kind == MountKind.Soul)
            {
                using var cleanup = new CancellationTokenSource(_options.ContainmentTimeout);
                try
                {
                    await _transfer.DeleteGuestCopyAsync(row.VmId, LayoutOf(request.GuestOs).ProfilePath, cleanup.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    await TryAuditAsync(VmMountAuditActions.GuestCleanupFailed, row, new JsonObject { ["reason"] = "AttachRollback", ["error"] = ex.GetType().Name })
                        .ConfigureAwait(false);
                }
            }

            await _canaries.StopWatchAsync(row.Id).ConfigureAwait(false);
            if (row.Kind == MountKind.Soul)
            {
                await _canaryStore.RetireForMountAsync(row.Id, _time.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
                CanaryRegistry.RemoveFolder(row.HostPath);
            }

            await _store.MarkDetachedAsync(row.Id, _time.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The row stays attached, so a detach-all still finds the mount and retries the provider.
            await TryAuditAsync(VmMountAuditActions.DetachFailed, row, new JsonObject { ["reason"] = "AttachRollback", ["error"] = ex.GetType().Name })
                .ConfigureAwait(false);
        }
    }

    private Task<int> RunGuestIoAsync(Guid vmId, Func<CancellationToken, Task> work, CancellationToken ct) =>
        _io.RunAsync(
            vmId,
            async t =>
            {
                await work(t).ConfigureAwait(false);
                return 0;
            },
            ct);

    private static void EnsureModeMatchesKind(MountSpec spec)
    {
        var expected = spec.Kind == MountKind.Soul ? MountMode.Ro : MountMode.Rw;
        if (spec.Mode != expected)
            throw new MountRefusedException(
                MountRefusalReason.ModeNotAllowed, $"A {spec.Kind} mount is always {expected.ToString().ToLowerInvariant()}.");
    }

    /// <summary>The guest path must be exactly the server's path for the kind and OS. Returns the server's spelling.</summary>
    private string EnsureGuestPath(MountSpec spec)
    {
        var layout = LayoutOf(spec.GuestOs);
        var chosen = spec.Kind == MountKind.Soul ? layout.SoulPath : layout.WorkspacePath;
        var cmp = spec.GuestOs == GuestOs.Windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (spec.GuestPath is null || !spec.GuestPath.Equals(chosen, cmp))
            throw new MountRefusedException(MountRefusalReason.GuestPathNotAllowed, "The guest path is chosen by the server, not by the caller.");
        return chosen;
    }

    private GuestMountLayout LayoutOf(GuestOs os) => _options.GetLayout(os);

    /// <summary>The guest OS comes from the VM's own row. The layout match is only a fallback for a VM row that is gone.</summary>
    private async Task<GuestOs> GuestOsOfAsync(ScopedMountRow row)
    {
        if (await _store.GetVmGuestOsAsync(row.VmId, CancellationToken.None).ConfigureAwait(false) is { } os)
            return os;
        return row.GuestPath.Equals(_options.Windows.SoulPath, StringComparison.OrdinalIgnoreCase)
            || row.GuestPath.Equals(_options.Windows.WorkspacePath, StringComparison.OrdinalIgnoreCase)
                ? GuestOs.Windows
                : GuestOs.Linux;
    }

    private string ProfileDirectory(string mountRoot) => Path.Combine(mountRoot, _options.ProfileFolderName);

    private static MountProviderRequest ToRequest(ScopedMountRow row, GuestOs os) =>
        new(row.Id, row.AgentId, row.VmId, os, row.Kind, row.Mode, row.HostPath, row.GuestPath);

    private Task WriteAuditAsync(string kind, ScopedMountRow row, JsonObject extra, CancellationToken ct)
    {
        Fill(extra, row);
        return _audit.AppendAsync(new AuditEntry(_time.GetUtcNow(), VmMountAuditActions.Actor, kind, extra.ToJsonString()), ct);
    }

    private async Task TryAuditAsync(string kind, ScopedMountRow row, JsonObject extra)
    {
        try
        {
            await WriteAuditAsync(kind, row, extra, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A failure record that cannot be written must not hide the failure it describes.
        }
    }

    private async Task TryAuditRefusalAsync(MountSpec spec, MountRefusedException refusal)
    {
        try
        {
            await _audit.AppendAsync(
                new AuditEntry(_time.GetUtcNow(), VmMountAuditActions.Actor, VmMountAuditActions.Refused, new JsonObject
                {
                    ["agent_id"] = spec.AgentId.ToString(),
                    ["vm_id"] = spec.VmId.ToString(),
                    ["kind"] = spec.Kind.ToString(),
                    ["mode"] = spec.Mode.ToString(),
                    ["host_path"] = SafeText.Clean(spec.HostPath),
                    ["guest_path"] = SafeText.Clean(spec.GuestPath),
                    ["reason"] = refusal.Reason.ToString(),
                    ["detail"] = SafeText.Clean(refusal.Message),
                }.ToJsonString()),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The refusal stands whether or not its record could be written.
        }
    }

    private static void Fill(JsonObject target, ScopedMountRow row)
    {
        target["mount_id"] = row.Id.ToString();
        target["agent_id"] = row.AgentId.ToString();
        target["vm_id"] = row.VmId.ToString();
        target["kind"] = row.Kind.ToString();
        target["mode"] = row.Mode.ToString();
        target["host_path"] = row.HostPath;
        target["guest_path"] = row.GuestPath;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
