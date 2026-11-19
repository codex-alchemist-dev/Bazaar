namespace Bazaar.Diagnostics.Redaction;

/// <summary>How a report field is handled before the pattern pass (doc 43 section 6.2, layer 1).</summary>
public enum RedactionClass
{
    /// <summary>Fixed vocabulary, enums and numbers. Still passes the pattern pass.</summary>
    Safe,

    /// <summary>Identifiers such as module ids and error codes. Still passes the pattern pass.</summary>
    Identifier,

    /// <summary>Free text such as exception messages and log templates.</summary>
    FreeText,

    /// <summary>Never enters a report. The field is replaced by a structural marker.</summary>
    Never,
}
