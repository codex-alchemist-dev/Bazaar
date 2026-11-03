using Bazaar.Server.Security.Taint;

namespace Bazaar.Server.Security.Readers;

/// <summary>
/// Text a fresh session may start from: owner-authored or server-generated. No other trust tag can be made, so external,
/// agent and member text cannot enter a fresh seed.
/// </summary>
public sealed class TrustedContent
{
    private TrustedContent(ContentTrust trust, string text)
    {
        Trust = trust;
        Text = text;
    }

    /// <summary>Either <see cref="ContentTrust.Owner"/> or <see cref="ContentTrust.System"/>.</summary>
    public ContentTrust Trust { get; }

    /// <summary>The text.</summary>
    public string Text { get; }

    /// <summary>Owner-authored text.</summary>
    public static TrustedContent FromOwner(string text) => Create(ContentTrust.Owner, text);

    /// <summary>Server-generated text.</summary>
    public static TrustedContent FromSystem(string text) => Create(ContentTrust.System, text);

    private static TrustedContent Create(ContentTrust trust, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("Trusted content must not be blank.", nameof(text));
        }

        return new TrustedContent(trust, text);
    }
}
