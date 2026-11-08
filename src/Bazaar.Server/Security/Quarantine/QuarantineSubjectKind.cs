namespace Bazaar.Server.Security.Quarantine;

/// <summary>What kind of thing is quarantined (doc 15, "Quarantine"; docs 01 and 08 for the storage forms).</summary>
public enum QuarantineSubjectKind
{
    /// <summary>A GC message or message version.</summary>
    GcMessage,

    /// <summary>A memory note in the Vault (doc 08).</summary>
    MemoryNote,

    /// <summary>Any other Vault file.</summary>
    VaultFile,

    /// <summary>An agent: its tokens are revoked and its VMs paused.</summary>
    Agent,

    /// <summary>A reader hand-over payload (doc 15, "Hand-over").</summary>
    Handover,
}

/// <summary>A quarantined thing: its kind and its identifier in that kind's store.</summary>
/// <param name="Kind">Kind of the subject.</param>
/// <param name="Id">Identifier in the subject's store (a GC message id, a Vault path, an agent id).</param>
public sealed record QuarantineSubject(QuarantineSubjectKind Kind, string Id);
