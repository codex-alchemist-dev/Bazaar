using System.Text.Json.Nodes;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.VmMounts.Abstractions;
using Bazaar.Server.VmMounts.Canaries;
using Bazaar.Server.VmMounts.Paths;

namespace Bazaar.Server.VmMounts;

/// <summary>
/// The scoped-mount manager (doc 16 WP8 "Scoped mounts", doc 15 WP8). The rules it enforces, in order of weight:
/// the host path jail and the mode floor (Soul read-only, workspace read-write) run before anything is exposed; a hard-taint or
/// kill-switch detach never calls the guest and never writes the profile back; detaching never waits on the guest; and every
/// attach, detach and write-back leaves an audit row. One lock per VM serialises all operations on that VM.
/// </summary>
public sealed class MountManager : IMountManager
{
    private readonly IScopedMountStore _store;
    private readonly IMountProvider _provider;
    private readonly IProfileTransfer _transfer;
    private readonly ICanarySink _sink;
    private readonly IAuditLog _audit;
    private readonly IMountContentPolicy _policy;
    private readonly VmMountOptions _options;
    private readonly TimeProvider _time;
    private readonly HostPathGuard _guard;
    private readonly VmLocks _locks = new();
    private readonly CanaryRegistry _canaries = new();

    public MountManager(
        IScopedMountStore store,
        IMountProvider provider,
        IProfileTransfer transfer,
        ICanarySink sink,
        IAuditLog audit,
        IMountContentPolicy policy,
        VmMountOptions options,
        TimeProvider? time = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _transfer = transfer ?? throw new ArgumentNullException(nameof(transfer));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _time = time ?? TimeProvider.System;
        _guard = new HostPathGuard(options);
    }

    /// <summary>VMs that currently hold a lock holder or waiter. Zero when idle; for tests.</summary>
    internal int ActiveLockCount => _locks.ActiveCount;

    public async Task<AttachedMount> AttachAsync(MountSpec spec, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(spec);
        using var lease = await _locks.AcquireAsync(spec.VmId, ct).ConfigureAwait(false);

        GuardedPath guarded;
        string guestPath;
        try
        {
            EnsureModeMatchesKind(spec);
            guestPath = EnsureGuestPath(spec);
            guarded = _guard.Check(spec.AgentId, spec.Kind, spec.HostPath);
            MountTreeScanner.Scan(guarded.MountRoot, spec.Kind, _options.MaxScanEntries, _options.CaseInsensitive, _policy);
            if (spec.Kind == MountKind.Soul && HostPathGuard.IsLink(ProfileDirectory(guarded.MountRoot)))
                throw new MountRefusedException(MountRefusalReason.LinkNotAllowed, "The profile folder is a link.");
        }
        catch (MountRefusedException ex)
        {
            await TryAuditRefusalAsync(spec, ex).ConfigureAwait(false);
            throw;
        }

        var mountId = Guid.CreateVersion7();
        var row = new ScopedMountRow(mountId, spec.AgentId, spec.VmId, spec.Kind, guarded.MountRoot, guestPath, spec.Mode, null);

        // The row goes in first: a crash after this point leaves a row that says "attached", the safe direction for a detach-all.
        if (!await _store.TryInsertAsync(row, ct).ConfigureAwait(false))
        {
            var ex = new MountRefusedException(MountRefusalReason.AlreadyAttached, "The guest path already carries an attached mount.");
            await TryAuditRefusalAsync(spec, ex).ConfigureAwait(false);
            throw ex;
        }

        var request = ToRequest(row, spec.GuestOs);
        IReadOnlyList<PlantedCanary> planted = [];
        var profileFiles = 0;
        try
        {
            if (spec.Kind == MountKind.Soul)
            {
                // Canaries first, so the share is never visible without them.
                planted = _canaries.Plant(spec, mountId, guarded.MountRoot, _options.CanaryCount, _policy);
            }

            await _provider.AttachAsync(request, ct).ConfigureAwait(false);

            if (spec.Kind == MountKind.Soul)
            {
                var watch = await _provider.WatchCanariesAsync(
                    new CanaryWatch(mountId, [.. planted.Select(c => c.RelativePath)]),
                    access => OnCanaryAccessAsync(spec, access),
                    ct).ConfigureAwait(false);
                _canaries.SetWatch(mountId, watch);

                var profileDirectory = ProfileDirectory(guarded.MountRoot);
                Directory.CreateDirectory(profileDirectory);
                var manifest = await _transfer.PushAsync(
                    spec.VmId, spec.GuestOs, profileDirectory, LayoutOf(spec.GuestOs).ProfilePath, ct).ConfigureAwait(false);
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
        using var lease = await _locks.AcquireAsync(first.VmId, ct).ConfigureAwait(false);

        // Re-read under the lock: a concurrent detach may have finished while this call waited.
        var row = await _store.GetAsync(mountId, ct).ConfigureAwait(false) ?? first;
        if (row.DetachedAt is not null)
            return new DetachResult(mountId, true, true, reason, WriteBackOutcome.NotAttempted);

        var guestOs = GuestOsOf(row);
        var writeBack = WriteBackOutcome.NotAttempted;
        if (reason == DetachReason.Clean && row.Kind == MountKind.Soul)
        {
            // While the share and the guest are still up. A rejected or failed write-back never blocks the detach.
            writeBack = (await WriteBackCoreAsync(row, guestOs, ct).ConfigureAwait(false)).Outcome;
        }

        // From here on, nothing may stop the host side from letting go, so the caller's token is not used.
        try
        {
            await _provider.DetachAsync(ToRequest(row, guestOs), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await TryAuditAsync(VmMountAuditActions.DetachFailed, row, new JsonObject
            {
                ["reason"] = reason.ToString(),
                ["error"] = ex.GetType().Name,
            }).ConfigureAwait(false);
            throw;
        }

        await _canaries.ForgetAsync(mountId).ConfigureAwait(false);
        if (row.Kind == MountKind.Soul)
            CanaryRegistry.RemoveFolder(row.HostPath);
        await _store.MarkDetachedAsync(mountId, _time.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);

        await WriteAuditAsync(VmMountAuditActions.Detach, row, new JsonObject
        {
            ["reason"] = reason.ToString(),
            ["writeback"] = writeBack.ToString(),
        }, CancellationToken.None).ConfigureAwait(false);
        return new DetachResult(mountId, true, false, reason, writeBack);
    }

    public async Task<IReadOnlyList<DetachResult>> DetachAllForAgentAsync(Guid agentId, DetachReason reason, CancellationToken ct)
    {
        var rows = await _store.ListAttachedForAgentAsync(agentId, ct).ConfigureAwait(false);
        var results = new List<DetachResult>(rows.Count);
        List<Exception>? failures = null;
        foreach (var row in rows)
        {
            try
            {
                results.Add(await DetachAsync(row.Id, reason, ct).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
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
        if (first is not { DetachedAt: null, Kind: MountKind.Soul })
            throw new MountRefusedException(MountRefusalReason.NotAttached, "The mount is not an attached Soul mount.");

        using var lease = await _locks.AcquireAsync(first.VmId, ct).ConfigureAwait(false);
        var row = await _store.GetAsync(mountId, ct).ConfigureAwait(false);
        if (row is not { DetachedAt: null })
            throw new MountRefusedException(MountRefusalReason.NotAttached, "The mount was detached.");
        return await WriteBackCoreAsync(row, GuestOsOf(row), ct).ConfigureAwait(false);
    }

    public async Task<bool> ReportCanaryTokenUseAsync(string tokenValue, string source, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(tokenValue);
        var canary = _canaries.FindByFingerprint(CanaryRegistry.Fingerprint(tokenValue));
        if (canary is null)
            return false;
        await ReportAsync(canary, CanaryUseKind.TokenSeen, source, ct).ConfigureAwait(false);
        return true;
    }

    private async ValueTask OnCanaryAccessAsync(MountSpec spec, CanaryAccess access)
    {
        var canary = _canaries.FindByPath(access.MountId, access.RelativePath);
        if (canary is null || canary.AgentId != spec.AgentId)
            return;
        await ReportAsync(canary, CanaryUseKind.FileRead, "provider", CancellationToken.None, access.At).ConfigureAwait(false);
    }

    private async Task ReportAsync(
        PlantedCanary canary, CanaryUseKind kind, string source, CancellationToken ct, DateTimeOffset? at = null)
    {
        var when = at ?? _time.GetUtcNow();
        // The audit row first: the sink may page and freeze, and the record must exist whatever the sink does.
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
            }.ToJsonString()),
            ct).ConfigureAwait(false);
        await _sink.ReportCanaryUseAsync(
            new CanaryUseReport(canary.AgentId, canary.VmId, canary.MountId, canary.Fingerprint, canary.RelativePath, kind, source, when),
            ct).ConfigureAwait(false);
    }

    private async Task<WriteBackResult> WriteBackCoreAsync(ScopedMountRow row, GuestOs guestOs, CancellationToken ct)
    {
        var profileDirectory = ProfileDirectory(row.HostPath);
        var staging = Path.Combine(_guard.AgentRoot(row.AgentId), _options.StagingFolderName, Guid.NewGuid().ToString("N"));
        try
        {
            if (HostPathGuard.IsLink(profileDirectory))
                throw new ProfileTransferException(ProfileTransferFailure.Invalid, "The host profile folder is a link.");

            var manifest = await _transfer.PullAsync(row.VmId, guestOs, LayoutOf(guestOs).ProfilePath, staging, ct).ConfigureAwait(false);
            SwapIntoPlace(staging, profileDirectory);

            await WriteAuditAsync(VmMountAuditActions.WriteBack, row, new JsonObject
            {
                ["files"] = manifest.Count,
                ["bytes"] = manifest.TotalBytes,
                ["digest"] = manifest.Digest,
            }, ct).ConfigureAwait(false);
            return new WriteBackResult(row.Id, WriteBackOutcome.Written, manifest.Count, null);
        }
        catch (ProfileTransferException ex) when (ex.Failure != ProfileTransferFailure.Channel)
        {
            await TryAuditAsync(VmMountAuditActions.WriteBackRejected, row, new JsonObject
            {
                ["failure"] = ex.Failure.ToString(),
                ["detail"] = ex.Message,
            }).ConfigureAwait(false);
            return new WriteBackResult(row.Id, WriteBackOutcome.Rejected, 0, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not MountRefusedException)
        {
            await TryAuditAsync(VmMountAuditActions.WriteBackRejected, row, new JsonObject
            {
                ["failure"] = "Failed",
                ["detail"] = ex.GetType().Name,
            }).ConfigureAwait(false);
            return new WriteBackResult(row.Id, WriteBackOutcome.Failed, 0, ex.Message);
        }
        finally
        {
            TryDeleteDirectory(staging);
        }
    }

    /// <summary>Replaces the host profile with the validated staging copy. The old profile is restored if the final move fails.</summary>
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
            throw;
        }

        TryDeleteDirectory(backup);
    }

    private async Task RollBackAttachAsync(MountProviderRequest request, ScopedMountRow row)
    {
        // Best effort, and never cancellable: an attach that failed halfway must not leave the share exposed.
        try
        {
            await _provider.DetachAsync(request, CancellationToken.None).ConfigureAwait(false);
            await _canaries.ForgetAsync(row.Id).ConfigureAwait(false);
            if (row.Kind == MountKind.Soul)
                CanaryRegistry.RemoveFolder(row.HostPath);
            await _store.MarkDetachedAsync(row.Id, _time.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The row stays attached, so a detach-all still finds the mount and retries the provider.
            await TryAuditAsync(VmMountAuditActions.DetachFailed, row, new JsonObject { ["reason"] = "AttachRollback", ["error"] = ex.GetType().Name })
                .ConfigureAwait(false);
        }
    }

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

    private GuestMountLayout LayoutOf(GuestOs os) => os == GuestOs.Windows ? _options.Windows : _options.Linux;

    private GuestOs GuestOsOf(ScopedMountRow row) =>
        row.GuestPath.Equals(_options.Windows.SoulPath, StringComparison.OrdinalIgnoreCase)
        || row.GuestPath.Equals(_options.Windows.WorkspacePath, StringComparison.OrdinalIgnoreCase)
            ? GuestOs.Windows
            : GuestOs.Linux;

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
                    ["host_path"] = spec.HostPath,
                    ["guest_path"] = spec.GuestPath,
                    ["reason"] = refusal.Reason.ToString(),
                    ["detail"] = refusal.Message,
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
