namespace Bazaar.Server.Memory.Access;

/// <summary>Who is reading the Vault, for path access rules (docs/design/08-memory-system.md, section 8).</summary>
public enum VaultPrincipalKind
{
    /// <summary>The owner. Reads every area; reads of another agent's private memory are logged.</summary>
    Owner,

    /// <summary>An agent reading with its own identity.</summary>
    Agent,

    /// <summary>The cover agent for a primary agent. Never reads the primary's private memory.</summary>
    Cover,

    /// <summary>The server's consolidation job. May read private memory.</summary>
    Maintenance,

    /// <summary>A co-member human who is not the owner.</summary>
    MemberHuman,
}
