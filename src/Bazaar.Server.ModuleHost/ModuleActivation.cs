namespace Bazaar.Server.ModuleHost;

/// <summary>Where a module stands with the host's permission gate.</summary>
public enum ModuleActivationStatus
{
    /// <summary>The start guard passed and the module's approved set is in force.</summary>
    Active = 0,

    /// <summary>No installed package is known for the module (a compiled-in component). Nothing is in force, and the gate would refuse every access it made.</summary>
    NotPackaged,

    /// <summary>The component is disabled by configuration. It was never started.</summary>
    Disabled,

    /// <summary>The package signature does not verify now. The module does not start.</summary>
    RefusedSignature,

    /// <summary>The declared permissions are not all approved by the owner. The module does not start.</summary>
    RefusedUnapproved,

    /// <summary>A module it hard-depends on is not active, so it does not start.</summary>
    BlockedByDependency,

    /// <summary>The set was in force and has been taken away. See <see cref="ModuleActivation.Reason"/>.</summary>
    Deactivated,
}

/// <summary>Why the host took a module's set out of force.</summary>
public enum ModuleDeactivationReason
{
    /// <summary>The component was disabled in configuration.</summary>
    Disabled = 0,

    /// <summary>Its health rolled up to Failing (doc 46 section 10.1), alone or through a hard dependency.</summary>
    HealthFailing,

    /// <summary>A module it hard-depends on was deactivated.</summary>
    DependencyDeactivated,

    /// <summary>The caller asked for it.</summary>
    Requested,

    /// <summary>The host is stopping.</summary>
    HostStopping,
}

/// <summary>One module's activation state.</summary>
/// <param name="ModuleId">The module (component) id.</param>
/// <param name="Status">Where it stands.</param>
/// <param name="Reason">Why it was deactivated, when <paramref name="Status"/> is <see cref="ModuleActivationStatus.Deactivated"/>.</param>
/// <param name="Detail">A short plain statement of the cause, never a secret.</param>
/// <param name="Granted">The permissions in force, in written form. Empty unless active.</param>
/// <param name="Ignored">Approved strings that are not valid permissions. They grant nothing; they are reported so a person can look.</param>
public sealed record ModuleActivation(
    string ModuleId,
    ModuleActivationStatus Status,
    ModuleDeactivationReason? Reason = null,
    string? Detail = null,
    IReadOnlyList<string>? Granted = null,
    IReadOnlyList<string>? Ignored = null)
{
    /// <summary>The permissions in force, never null.</summary>
    public IReadOnlyList<string> GrantedPermissions => Granted ?? [];

    /// <summary>The ignored approved strings, never null.</summary>
    public IReadOnlyList<string> IgnoredPermissions => Ignored ?? [];
}
