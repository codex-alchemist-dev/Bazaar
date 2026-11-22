using System.Collections.Concurrent;

namespace Bazaar.ModulePermissions;

/// <summary>
/// The in-process permission gate. One immutable permission set per running module. Refusals go to the sink, at most
/// <see cref="MaxDenialsPerWindow"/> per module in each <see cref="DenialWindow"/>; the rest are counted and the count rides
/// on the next recorded refusal.
/// </summary>
public sealed class ModulePermissionGate(IModulePermissionDenialSink sink, TimeProvider time) : IModulePermissionGate, IModulePermissionRegistry
{
    /// <summary>Refusals per module that reach the audit sink in one window.</summary>
    public const int MaxDenialsPerWindow = 100;

    /// <summary>Longest module id accepted, the same limit as the install gate.</summary>
    public const int MaxModuleIdLength = 256;

    /// <summary>Most permissions one module may hold, the same limit as the install gate.</summary>
    public const int MaxPermissions = 256;

    /// <summary>The length of the refusal window.</summary>
    public static readonly TimeSpan DenialWindow = TimeSpan.FromMinutes(1);

    private const string _notRunningModuleId = "(not running)";

    private readonly ConcurrentDictionary<string, Running> _running = new(StringComparer.Ordinal);

    // Refusals for modules that are not running share one budget, so made-up module ids cannot grow state or flood the log.
    private readonly DenialBudget _notRunning = new();

    /// <inheritdoc />
    public ModuleGrantSummary Activate(string moduleId, IEnumerable<string> approvedPermissions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(moduleId.Length, MaxModuleIdLength, nameof(moduleId));
        ArgumentNullException.ThrowIfNull(approvedPermissions);

        var granted = new List<ModuleGrant>();
        var ignored = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var text in approvedPermissions)
        {
            if (seen.Count == MaxPermissions)
            {
                throw new ArgumentException($"A module holds at most {MaxPermissions} permissions.", nameof(approvedPermissions));
            }

            if (!seen.Add(text ?? string.Empty))
            {
                continue;
            }

            if (ModuleGrant.TryParse(text, out var permission))
            {
                granted.Add(permission);
            }
            else
            {
                ignored.Add(text ?? string.Empty);
            }
        }

        if (!_running.TryAdd(moduleId, new Running([.. granted])))
        {
            throw new InvalidOperationException("The module already has a permission set in force. Stop it before starting it again.");
        }

        return new ModuleGrantSummary([.. granted.Select(p => p.ToString())], ignored);
    }

    /// <inheritdoc />
    public bool Deactivate(string moduleId)
    {
        ArgumentNullException.ThrowIfNull(moduleId);
        return _running.TryRemove(moduleId, out _);
    }

    /// <inheritdoc />
    public ModuleAccessDecision Check(string moduleId, ModuleAccess access)
    {
        ArgumentNullException.ThrowIfNull(moduleId);
        ArgumentNullException.ThrowIfNull(access);

        _running.TryGetValue(moduleId, out var running);
        var decision = Decide(running, access);
        if (decision != ModuleAccessDecision.Allowed)
        {
            Report(running, running is null ? _notRunningModuleId : moduleId, access, decision);
        }

        return decision;
    }

    /// <inheritdoc />
    public void Demand(string moduleId, ModuleAccess access)
    {
        var decision = Check(moduleId, access);
        if (decision != ModuleAccessDecision.Allowed)
        {
            throw new ModulePermissionDeniedException(moduleId, access, decision);
        }
    }

    private static ModuleAccessDecision Decide(Running? running, ModuleAccess access)
    {
        if (running is null)
        {
            return ModuleAccessDecision.DeniedModuleNotRunning;
        }

        if (access.Class is null || !ModulePermissionClasses.IsClass(access.Class))
        {
            return ModuleAccessDecision.DeniedUnknownClass;
        }

        if (access.Target is not null && !IsCleanTarget(access))
        {
            return ModuleAccessDecision.DeniedInvalidTarget;
        }

        foreach (var permission in running.Permissions)
        {
            if (permission.Covers(access))
            {
                return ModuleAccessDecision.Allowed;
            }
        }

        return ModuleAccessDecision.DeniedNotGranted;
    }

    private static bool IsCleanTarget(ModuleAccess access) =>
        access.Class == ModulePermissionClasses.Network
            ? ModuleTargetRules.NormalizeHost(access.Target) is not null
            : ModuleTargetRules.IsValidTarget(access.Target);

    private void Report(Running? running, string moduleId, ModuleAccess access, ModuleAccessDecision decision)
    {
        var now = time.GetUtcNow();
        var budget = running?.Denials ?? _notRunning;
        if (!budget.TryTake(now, out var suppressed))
        {
            return;
        }

        // Only values that passed the lexical rules are written out. Anything else could carry text chosen by the module.
        var knownClass = access.Class is not null && ModulePermissionClasses.IsClass(access.Class);
        var cleanTarget = knownClass && access.Target is not null && IsCleanTarget(access);
        sink.Record(new ModulePermissionDenial(
            At: now,
            ModuleId: moduleId,
            Class: knownClass ? access.Class! : "(unknown)",
            Target: cleanTarget ? access.Target : null,
            TargetLength: access.Target?.Length ?? 0,
            Decision: decision,
            SuppressedBefore: suppressed));
    }

    private sealed record Running(ModuleGrant[] Permissions)
    {
        public DenialBudget Denials { get; } = new();
    }

    private sealed class DenialBudget
    {
        private readonly Lock _gate = new();
        private DateTimeOffset _windowStart = DateTimeOffset.MinValue;
        private int _taken;
        private long _suppressed;

        public bool TryTake(DateTimeOffset now, out long suppressedBefore)
        {
            lock (_gate)
            {
                // A clock that steps backwards also opens a new window, so it can never close the budget for good.
                if (now - _windowStart >= DenialWindow || now < _windowStart)
                {
                    _windowStart = now;
                    _taken = 0;
                }

                if (_taken >= MaxDenialsPerWindow)
                {
                    _suppressed++;
                    suppressedBefore = 0;
                    return false;
                }

                _taken++;
                suppressedBefore = _suppressed;
                _suppressed = 0;
                return true;
            }
        }
    }
}
