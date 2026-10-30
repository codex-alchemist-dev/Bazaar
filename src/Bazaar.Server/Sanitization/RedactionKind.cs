namespace Bazaar.Server.Sanitization;

/// <summary>What a sanitizer span was replaced for. Values are in the order results report them.</summary>
public enum RedactionKind
{
    /// <summary>A person's name.</summary>
    Person,

    /// <summary>An agent's name.</summary>
    Agent,

    /// <summary>A repository name.</summary>
    Repository,

    /// <summary>A customer name.</summary>
    Customer,

    /// <summary>An email address.</summary>
    Email,

    /// <summary>A Vault file path.</summary>
    VaultPath,

    /// <summary>A Vault wiki link.</summary>
    VaultLink,

    /// <summary>A credential or key.</summary>
    Secret,

    /// <summary>Block-quoted text, usually copied from a GC.</summary>
    QuotedText,

    /// <summary>Quoted external text named by the caller.</summary>
    ExternalQuote,
}
