namespace Bazaar.Server.WebAdmin;

/// <summary>The panel areas of doc 51 section 1, each tied to one <c>admin.*</c> capability (section 2).</summary>
public enum AdminArea
{
    /// <summary>Server status, Kodo health, audit log view and pending approvals: any one <c>admin.*</c> capability opens them.</summary>
    Overview,

    Members,

    Devices,

    /// <summary>DM access requests and privacy settings.</summary>
    Privacy,

    Backups,

    /// <summary>Module selection, rebuilds and build reports.</summary>
    Modules,

    Roles,
}
