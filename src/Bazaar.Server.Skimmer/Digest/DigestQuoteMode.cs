namespace Bazaar.Server.Skimmer.Digest;

/// <summary>How a digest item refers to its message text (doc 13, <c>skimmer.digest_quote_mode</c>).</summary>
public enum DigestQuoteMode
{
    /// <summary>The digest holds no text; the excerpt is read from the message at display time.</summary>
    Reference,

    /// <summary>The excerpt is copied under the message's per-person key. Not implemented until doc 14 lands.</summary>
    EncryptedExcerpt,
}
