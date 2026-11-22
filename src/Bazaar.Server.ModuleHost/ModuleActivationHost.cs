using Bazaar.Core;
using Bazaar.Install;
using Bazaar.Install.Abstractions;
using Bazaar.Install.Trust;
using Bazaar.ModulePermissions;
using Bazaar.Server.Modules.Health;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.ModuleHost;

/// <summary>
/// Puts each module's approved permission set in force when the module starts and takes it away when the module stops
/// (doc 48 section 1, doc 40 module host entry). It does not decide what is approved: for every module with an installed
/// package it runs <see cref="ModuleStartGuard"/> (signature re-verified, every stored approval re-checked against the owner's
/// key) and passes only the set that returns to <see cref="IModulePermissionRegistry"/>. A set in force is never replaced
/// in place: re-activation deactivates first and runs the guard again. Everything fails closed: a module that is not listed here
/// as <see cref="ModuleActivationStatus.Active"/> has no set in force, so the gate refuses every access it makes.
/// </summary>
public sealed partial class ModuleActivationHost : IDisposable
{
    private readonly IReadOnlyList<ComponentManifest> _ordered;
    private readonly Dictionary<string, ComponentManifest> _byId;
    private readonly IModulePackageSource _packages;
    private readonly IModuleInstallStore _store;
    private readonly IModuleSignatureVerifier _signatures;
    private readonly IOwnerApprovalVerifier _owner;
    private readonly IModulePermissionRegistry _registry;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ModuleActivationHost> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<string, ModuleActivation> _state = new(StringComparer.Ordinal);

    /// <param name="manifests">Every component manifest of this host. The order and the hard edges come from <see cref="ComponentGraph"/>.</param>
    /// <exception cref="ComponentGraphException">The manifests do not form a valid graph.</exception>
    public ModuleActivationHost(
        IEnumerable<ComponentManifest> manifests,
        IModulePackageSource packages,
        IModuleInstallStore store,
        IModuleSignatureVerifier signatures,
        IOwnerApprovalVerifier owner,
        IModulePermissionRegistry registry,
        IConfiguration configuration,
        ILogger<ModuleActivationHost> logger)
    {
        ArgumentNullException.ThrowIfNull(manifests);
        _ordered = ComponentGraph.Order(manifests);
        _byId = _ordered.ToDictionary(m => m.Id, StringComparer.Ordinal);
        _packages = packages;
        _store = store;
        _signatures = signatures;
        _owner = owner;
        _registry = registry;
        _configuration = configuration;
        _logger = logger;
    }

    /// <inheritdoc />
    public void Dispose() => _lock.Dispose();

    /// <summary>The state of every module the host has handled, in dependency order.</summary>
    public IReadOnlyList<ModuleActivation> Snapshot()
    {
        _lock.Wait();
        try
        {
            return _ordered.Select(m => _state.GetValueOrDefault(m.Id)).Where(s => s is not null).Select(s => s!).ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// The host start step, run after the component graph has started. Walks the enabled modules in dependency order. A disabled
    /// module is recorded and skipped. A module whose hard dependency is not usable is blocked and never asks the guard.
    /// </summary>
    public async Task<IReadOnlyList<ModuleActivation>> ActivateAllAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var manifest in _ordered)
            {
                ct.ThrowIfCancellationRequested();
                if (!BazaarComponentRegistration.IsEnabled(_configuration, manifest))
                {
                    Set(new ModuleActivation(manifest.Id, ModuleActivationStatus.Disabled, Detail: "Disabled by configuration."));
                    continue;
                }

                await ActivateCoreAsync(manifest, ct).ConfigureAwait(false);
            }

            return _ordered.Select(m => _state[m.Id]).ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Starts one module again after it was deactivated, running the whole guard again. Does nothing to an active module.
    /// A disabled module is not started. Does not touch its dependents.
    /// </summary>
    /// <exception cref="ArgumentException">The id is not a component of this host.</exception>
    public async Task<ModuleActivation> ActivateAsync(string moduleId, CancellationToken ct = default)
    {
        var manifest = Manifest(moduleId);
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_state.TryGetValue(moduleId, out var current) && current.Status == ModuleActivationStatus.Active)
            {
                return current;
            }

            if (!BazaarComponentRegistration.IsEnabled(_configuration, manifest))
            {
                return Set(new ModuleActivation(moduleId, ModuleActivationStatus.Disabled, Detail: "Disabled by configuration."));
            }

            return await ActivateCoreAsync(manifest, ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Takes the module's set out of force, and the sets of the active modules that hard-depend on it, dependents first.
    /// Returns the modules that were active and are now deactivated, which is empty when the module was not active.
    /// </summary>
    /// <exception cref="ArgumentException">The id is not a component of this host.</exception>
    public async Task<IReadOnlyList<ModuleActivation>> DeactivateAsync(string moduleId, ModuleDeactivationReason reason, CancellationToken ct = default)
    {
        _ = Manifest(moduleId);
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return DeactivateCore(moduleId, reason);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Applies a health pass. Spreads health along the hard and soft edges with <see cref="ModuleHealthPropagator"/>, then
    /// deactivates every active module whose rolled-up status is Failing. A module is never started again by this call, because
    /// a set that was taken away comes back only through <see cref="ActivateAsync"/> and the full guard. Reports for modules
    /// that are not components of this host are ignored.
    /// </summary>
    public async Task<IReadOnlyList<ModuleActivation>> ApplyHealthAsync(IReadOnlyList<ModuleHealthReport> reports, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reports);

        var known = reports.Where(r => _byId.ContainsKey(r.ModuleId)).ToList();
        var rollups = ModuleHealthPropagator.Propagate(_ordered, known);

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var changed = new List<ModuleActivation>();
            foreach (var rollup in rollups.Where(r => r.Status == ModuleHealthStatus.Failing))
            {
                // A dependent may already have gone with its dependency in this pass; DeactivateCore skips inactive modules.
                changed.AddRange(DeactivateCore(rollup.ModuleId, ModuleDeactivationReason.HealthFailing));
            }

            return changed;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Deactivates every active module whose component is now disabled in configuration. Enabling a component takes effect at the
    /// next host start, because a component registers its services only then.
    /// </summary>
    public async Task<IReadOnlyList<ModuleActivation>> ApplyEnablementAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var changed = new List<ModuleActivation>();
            foreach (var manifest in _ordered.Reverse())
            {
                if (_state.TryGetValue(manifest.Id, out var s) && s.Status == ModuleActivationStatus.Active
                    && !BazaarComponentRegistration.IsEnabled(_configuration, manifest))
                {
                    changed.AddRange(DeactivateCore(manifest.Id, ModuleDeactivationReason.Disabled));
                }
            }

            return changed;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Takes every set out of force, dependents first. The host stop step.</summary>
    public async Task DeactivateAllAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var manifest in _ordered.Reverse())
            {
                DeactivateCore(manifest.Id, ModuleDeactivationReason.HostStopping);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<ModuleActivation> ActivateCoreAsync(ComponentManifest manifest, CancellationToken ct)
    {
        var id = manifest.Id;

        var blocker = manifest.DependsOn.FirstOrDefault(d => !_state.TryGetValue(d, out var s) || !IsUsable(s));
        if (blocker is not null)
        {
            LogBlocked(_logger, id, blocker);
            return Set(new ModuleActivation(id, ModuleActivationStatus.BlockedByDependency, Detail: $"Hard dependency '{blocker}' is not usable."));
        }

        var package = await _packages.FindAsync(id, ct).ConfigureAwait(false);
        if (package is null)
        {
            return Set(new ModuleActivation(id, ModuleActivationStatus.NotPackaged, Detail: "No installed package."));
        }

        var approvals = await _store.ListPermissionApprovalsAsync(id, ct).ConfigureAwait(false);
        var decision = ModuleStartGuard.EnsureStartable(package, _signatures, approvals, _owner);

        switch (decision)
        {
            case ModuleStartDecision.Startable startable:
                // A set in force is never replaced: if the registry still holds one for this id, stop it first.
                _registry.Deactivate(id);
                var summary = _registry.Activate(id, startable.Permissions);
                if (summary.Ignored.Count > 0)
                {
                    LogIgnored(_logger, id, summary.Ignored.Count);
                }

                LogActivated(_logger, id, summary.Granted.Count);
                return Set(new ModuleActivation(id, ModuleActivationStatus.Active, Granted: summary.Granted, Ignored: summary.Ignored));

            case ModuleStartDecision.RefusedSignature refused:
                LogRefused(_logger, id, refused.Status.ToString());
                return Set(new ModuleActivation(id, ModuleActivationStatus.RefusedSignature, Detail: $"Signature: {refused.Status}."));

            case ModuleStartDecision.RefusedUnapproved refused:
                LogRefused(_logger, id, "unapproved permissions");
                return Set(new ModuleActivation(
                    id,
                    ModuleActivationStatus.RefusedUnapproved,
                    Detail: $"{refused.Unapproved.Count} declared permission(s) not approved.",
                    Ignored: null));

            default:
                throw new InvalidOperationException("Unknown start decision.");
        }
    }

    // Deactivates the module and, first, every active module that hard-depends on it (directly or not). Returns what changed.
    private List<ModuleActivation> DeactivateCore(string moduleId, ModuleDeactivationReason reason)
    {
        var changed = new List<ModuleActivation>();
        foreach (var dependent in HardDependents(moduleId))
        {
            if (IsActive(dependent))
            {
                changed.Add(Stop(dependent, ModuleDeactivationReason.DependencyDeactivated));
            }
        }

        if (IsActive(moduleId))
        {
            changed.Add(Stop(moduleId, reason));
        }

        return changed;
    }

    private ModuleActivation Stop(string moduleId, ModuleDeactivationReason reason)
    {
        _registry.Deactivate(moduleId);
        LogDeactivated(_logger, moduleId, reason.ToString());
        return Set(new ModuleActivation(moduleId, ModuleActivationStatus.Deactivated, reason, Detail: reason.ToString()));
    }

    // Transitive hard dependents, farthest first, so a dependent is stopped before what it needs.
    private List<string> HardDependents(string moduleId)
    {
        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { moduleId };
        void Visit(string id)
        {
            foreach (var m in _ordered.Where(m => m.DependsOn.Contains(id) && seen.Add(m.Id)))
            {
                Visit(m.Id);
                found.Add(m.Id);
            }
        }

        Visit(moduleId);
        return found;
    }

    // A dependency is usable when it is running under a gate, or it is not a gated module at all (no package, so nothing to refuse).
    private static bool IsUsable(ModuleActivation s) => s.Status is ModuleActivationStatus.Active or ModuleActivationStatus.NotPackaged;

    private bool IsActive(string id) => _state.TryGetValue(id, out var s) && s.Status == ModuleActivationStatus.Active;

    private ModuleActivation Set(ModuleActivation activation)
    {
        _state[activation.ModuleId] = activation;
        return activation;
    }

    private ComponentManifest Manifest(string moduleId) =>
        _byId.TryGetValue(moduleId, out var m) ? m : throw new ArgumentException($"'{moduleId}' is not a component of this host.", nameof(moduleId));

    [LoggerMessage(Level = LogLevel.Information, Message = "Module {ModuleId} active with {Count} permission(s).")]
    private static partial void LogActivated(ILogger logger, string moduleId, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Module {ModuleId} has {Count} approved string(s) that are not valid permissions.")]
    private static partial void LogIgnored(ILogger logger, string moduleId, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Module {ModuleId} refused at start: {Why}.")]
    private static partial void LogRefused(ILogger logger, string moduleId, string why);

    [LoggerMessage(Level = LogLevel.Error, Message = "Module {ModuleId} not started: hard dependency {Dependency} is not usable.")]
    private static partial void LogBlocked(ILogger logger, string moduleId, string dependency);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Module {ModuleId} deactivated: {Reason}.")]
    private static partial void LogDeactivated(ILogger logger, string moduleId, string reason);
}
