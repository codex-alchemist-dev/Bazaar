namespace Bazaar.ModulePermissions;

/// <summary>
/// The runtime permission gate of doc 48 section 1: the module host asks it before every service call it makes for a module.
/// It fails closed. An access is allowed only when the module is running and one of its approved permissions covers it.
/// </summary>
public interface IModulePermissionGate
{
    /// <summary>Decides one access. A refusal is handed to the audit sink before this returns.</summary>
    ModuleAccessDecision Check(string moduleId, ModuleAccess access);

    /// <summary>As <see cref="Check"/>, and throws <see cref="ModulePermissionDeniedException"/> on a refusal.</summary>
    void Demand(string moduleId, ModuleAccess access);
}

/// <summary>What <see cref="IModulePermissionRegistry.Activate"/> put in force.</summary>
/// <param name="Granted">The permissions in force, in written form.</param>
/// <param name="Ignored">Approved strings that are not valid permissions. They grant nothing; the host should report them.</param>
public sealed record ModuleGrantSummary(IReadOnlyList<string> Granted, IReadOnlyList<string> Ignored);

/// <summary>
/// Puts a module's approved permission set in force when the host starts it, and removes it when the host stops it. The
/// caller passes only permissions whose owner approval verified (the start guard in <c>Bazaar.Install</c>).
/// </summary>
public interface IModulePermissionRegistry
{
    /// <summary>
    /// Puts the approved set in force. Throws <see cref="InvalidOperationException"/> when the module is already running:
    /// a set in force is never replaced or widened in place, the host stops the module first.
    /// </summary>
    ModuleGrantSummary Activate(string moduleId, IEnumerable<string> approvedPermissions);

    /// <summary>Removes the set. Every later access by the module is refused. False when the module was not running.</summary>
    bool Deactivate(string moduleId);
}

/// <summary>
/// Receives each refused access for the audit log. The gate limits how many refusals per module reach the sink, so a module
/// cannot flood the log; <see cref="ModulePermissionDenial.SuppressedBefore"/> carries the count that was dropped. If the sink
/// throws, the exception reaches the caller of the gate, and the access stays refused.
/// </summary>
public interface IModulePermissionDenialSink
{
    /// <summary>Records one refusal.</summary>
    void Record(ModulePermissionDenial denial);
}
