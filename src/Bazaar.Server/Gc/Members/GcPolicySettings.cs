using Bazaar.Server.Gc.Model;

namespace Bazaar.Server.Gc.Members;

/// <summary>Setting <c>gc.invite_policy</c> (docs/design/01-gc-data-model.md, settings table).</summary>
public enum GcInvitePolicy
{
    /// <summary>Any active member may invite (the default). Always logged.</summary>
    AnyMember,

    /// <summary>Only the owner may invite.</summary>
    OwnerOnly,
}

/// <summary>Setting <c>gc.unarchive_policy</c>.</summary>
public enum GcUnarchivePolicy
{
    /// <summary>Only the owner may unarchive (the default).</summary>
    Owner,

    /// <summary>Any active member with the member role may unarchive.</summary>
    AnyMember,
}

/// <summary>Setting <c>gc.dm_access_approvers</c>. The owner is always an approver.</summary>
public enum GcDmAccessApprovers
{
    /// <summary>Only the owner approves (the default).</summary>
    Owner,

    /// <summary>The owner and the active members of the DM may approve.</summary>
    OwnerOrDmMember,
}

/// <summary>Type keys the membership rules depend on. Other keys are plain membership GCs.</summary>
public static class GcTypeKeys
{
    /// <summary>A plain membership GC.</summary>
    public const string General = "general";

    /// <summary>Two-member DM plus the owner.</summary>
    public const string Dm = "dm";

    /// <summary>Per-agent Thinking GC nested under a working GC.</summary>
    public const string Thinking = "thinking";

    /// <summary>Recovery GC opened on Ward admission (doc 26, section 1).</summary>
    public const string Ward = "ward";

    /// <summary>Private reflection GC for vent drafts (doc 26, section 2).</summary>
    public const string Vent = "vent";

    /// <summary>Ephemeral all-hands brainstorm GC opened by Lucky mode (doc 26, section 6).</summary>
    public const string Brainstorm = "brainstorm";

    /// <summary>Casual social GC with no Kanban or reputation binding (doc 26, section 8).</summary>
    public const string Sidewalk = "sidewalk";

    /// <summary>Members a DM holds before the owner, per the seeded type table.</summary>
    public const int DmMemberLimit = 2;

    /// <summary>True for a DM.</summary>
    public static bool IsDm(GcRecord gc) => gc.TypeKey == Dm;

    /// <summary>True for a Thinking GC.</summary>
    public static bool IsThinking(GcRecord gc) => gc.TypeKey == Thinking;

    /// <summary>True for a Ward GC.</summary>
    public static bool IsWard(GcRecord gc) => gc.TypeKey == Ward;

    /// <summary>True for a Sidewalk GC.</summary>
    public static bool IsSideWalk(GcRecord gc) => gc.TypeKey == Sidewalk;
}
