namespace Bazaar.Server.Memory.Trust;

/// <summary>How a note of a given tier may be shown to an agent (doc 08, section 5 retrieval column).</summary>
public enum MemoryRetrieval
{
    /// <summary>Never returned to any agent or export.</summary>
    Excluded = 0,

    /// <summary>Returned only inside a block labelled as untrusted quoted data.</summary>
    LabelledQuotedData = 1,

    /// <summary>Returned like any other memory.</summary>
    Normal = 2,

    /// <summary>Returned like any other memory and ranked above normal notes.</summary>
    Preferred = 3,
}
