namespace Bazaar.Client.Tui;

/// <summary>
/// The Ops menu bar of doc 58 section 3 (owner card 2026-11-18, "Health first, admin tucked away"): Health, Kodo, Logs and Events at
/// the top, with Modules, Users and Approvals under one Admin menu, and audit and rebuilds reachable from there. The ids are stable
/// screen keys for the future Dashboard head; shortcut letters are defaults and can be remapped by building another list.
/// </summary>
public static class DefaultMenus
{
    public const string Health = "health";
    public const string Kodo = "kodo";
    public const string Logs = "logs";
    public const string Events = "events";
    public const string Admin = "admin";
    public const string Modules = "admin.modules";
    public const string Users = "admin.users";
    public const string Approvals = "admin.approvals";
    public const string Audit = "admin.audit";
    public const string Rebuilds = "admin.rebuilds";

    public static IReadOnlyList<MenuModel> Create() =>
    [
        new(Health, "Health", 'h', []),
        new(Kodo, "Kodo", 'k', []),
        new(Logs, "Logs", 'l', []),
        new(Events, "Events", 'e', []),
        new(Admin, "Admin", 'a',
        [
            new(Modules, "Modules", 'm'),
            new(Users, "Users", 'u'),
            new(Approvals, "Approvals", 'p'),
            new(Audit, "Audit", 'd'),
            new(Rebuilds, "Rebuilds", 'r'),
        ]),
    ];
}
