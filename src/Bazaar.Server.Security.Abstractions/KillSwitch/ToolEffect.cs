namespace Bazaar.Server.Security.KillSwitch;

/// <summary>
/// What a tool call does, as the <c>ToolDispatcher</c> classifies it before it runs. The kill switch uses this to decide
/// whether a stop applies to the call.
/// </summary>
public enum ToolEffect
{
    /// <summary>Reads state and changes nothing outside the call. Status and read-only paths use this.</summary>
    Read,

    /// <summary>Changes state inside the platform (Vault, GC, Kanban, repositories through the platform).</summary>
    Write,

    /// <summary>Sends data out of the platform (network egress, mail, public posts).</summary>
    Egress,
}
