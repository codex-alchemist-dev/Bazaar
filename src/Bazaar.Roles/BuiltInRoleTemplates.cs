using Bazaar.Roles.Abstractions;

namespace Bazaar.Roles;

/// <summary>
/// The five built-in templates of doc 48 section 4. They are ordinary rows once inserted, so the owner can edit them; only the
/// owner template is protected. Grants stay within doc 15's capability classes and doc 48's admin names.
/// </summary>
public static class BuiltInRoleTemplates
{
    public sealed record Template(string Key, string Name, string Description, bool Protected, IReadOnlyList<Grant> Grants);

    public static IReadOnlyList<Template> All { get; } =
    [
        new(RoleTemplateKeys.Owner, "Owner", "Full control of the company. At least one human always holds it.", true,
        [
            At(Capabilities.Read), At(Capabilities.Post), At(Capabilities.Send), At(Capabilities.Spend), At(Capabilities.Push),
            At(Capabilities.Merge), At(Capabilities.CreateAccount), At(Capabilities.GrantScope), At(Capabilities.InstallPackage),
            At(Capabilities.Deploy), Instance(Capabilities.Approve),
            Instance(Capabilities.AdminMembers), Instance(Capabilities.AdminDevices), Instance(Capabilities.AdminPrivacy),
            Instance(Capabilities.AdminBackups), Instance(Capabilities.AdminModules), Instance(Capabilities.AdminRoles),
        ]),
        new(RoleTemplateKeys.Coworker, "Coworker", "Works in the company's chats.", false,
            [At(Capabilities.Read), At(Capabilities.Post), At(Capabilities.Send)]),
        new(RoleTemplateKeys.Reviewer, "Reviewer", "Reads and comments.", false,
            [At(Capabilities.Read), At(Capabilities.Post)]),
        new(RoleTemplateKeys.ReadOnlyObserver, "Read-only observer", "Reads only.", false,
            [At(Capabilities.Read)]),
        new(RoleTemplateKeys.Agent, "Agent", "Default role of an agent. No admin or approval capability.", false,
            [At(Capabilities.Read), At(Capabilities.Post)]),
    ];

    /// <summary>The grants the owner role must always keep (doc 48 section 4.1).</summary>
    public static IReadOnlyList<Grant> OwnerFloor { get; } = [Instance(Capabilities.AdminRoles), Instance(Capabilities.Approve)];

    private static Grant At(Capability capability) => new(capability, Scope.Company);

    private static Grant Instance(Capability capability) => new(capability, Scope.Instance);
}
