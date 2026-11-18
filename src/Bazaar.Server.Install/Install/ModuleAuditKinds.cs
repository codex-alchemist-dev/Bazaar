namespace Bazaar.Server.Install;

/// <summary>Audit kinds for module install and permission approval (doc 48 section 5.1: "module install", "permission approval").</summary>
public static class ModuleAuditKinds
{
    /// <summary>A module package passed the signature, host and permission checks and was installed.</summary>
    public const string Installed = "module.installed";

    /// <summary>The owner approved a module version's permission set (doc 48 section 1).</summary>
    public const string PermissionsApproved = "module.permissions.approved";
}
