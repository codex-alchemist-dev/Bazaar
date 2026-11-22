namespace Bazaar.ModulePermissions;

/// <summary>
/// One thing a module asks the host to do: a permission class and, where the class has one, the exact target (a data area, a
/// path, a host, a secret name, a pane, a job, a program or a device capability). A target is never a pattern.
/// </summary>
public sealed record ModuleAccess(string Class, string? Target = null);

/// <summary>The answer of the permission gate. Everything except <see cref="Allowed"/> is a refusal.</summary>
public enum ModuleAccessDecision
{
    /// <summary>An approved permission covers the access.</summary>
    Allowed = 0,

    /// <summary>No approved permission covers the access.</summary>
    DeniedNotGranted,

    /// <summary>The target is not a clean name, path or host, so no grant can cover it.</summary>
    DeniedInvalidTarget,

    /// <summary>The class is not one of the ten permission classes.</summary>
    DeniedUnknownClass,

    /// <summary>The module has no approved set in force: it was never started, or it was stopped.</summary>
    DeniedModuleNotRunning,
}

/// <summary>
/// A refused access, as handed to the audit sink (<c>module.permission.denied</c>). The target is kept only when it is a
/// clean value; a malformed one is dropped and only its length is recorded, so a module cannot write arbitrary text into the
/// audit log through a refusal.
/// </summary>
public sealed record ModulePermissionDenial(
    DateTimeOffset At,
    string ModuleId,
    string Class,
    string? Target,
    int TargetLength,
    ModuleAccessDecision Decision,
    long SuppressedBefore)
{
    /// <summary>The audit kind of a refused module access (doc 48 section 1: "refused and audited").</summary>
    public const string AuditKind = "module.permission.denied";
}

/// <summary>Thrown by <see cref="IModulePermissionGate.Demand"/> when an access is refused.</summary>
public sealed class ModulePermissionDeniedException(string moduleId, ModuleAccess access, ModuleAccessDecision decision)
    : UnauthorizedAccessException($"Module access refused ({decision}).")
{
    /// <summary>The module that asked.</summary>
    public string ModuleId { get; } = moduleId;

    /// <summary>What it asked for.</summary>
    public ModuleAccess Access { get; } = access;

    /// <summary>Why it was refused.</summary>
    public ModuleAccessDecision Decision { get; } = decision;
}
