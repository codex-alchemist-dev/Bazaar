using System.Text.Json;
using Bazaar.Localisation;

namespace Bazaar.Client.DesktopChrome;

/// <summary>
/// The chrome's message catalogues. English ships embedded (the source locale); other locales are catalogues a head supplies, in the same
/// flat key-to-text JSON shape. Lookup falls back from the exact tag to its language to English, through <see cref="LocalisedText"/>.
/// </summary>
public static class ChromeCatalog
{
    private const string _englishResource = "Bazaar.Client.DesktopChrome.Strings.chrome.en.json";

    /// <summary>The embedded English catalogue.</summary>
    public static MessageCatalog English { get; } = Load();

    /// <summary>Reads a catalogue for <paramref name="locale"/> from flat JSON (<c>{ "key": "text" }</c>).</summary>
    public static MessageCatalog FromJson(string locale, Stream json)
    {
        ArgumentNullException.ThrowIfNull(json);
        Dictionary<string, string>? messages = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        return MessageCatalog.Create(locale, messages ?? []);
    }

    /// <summary>
    /// Builds the lookup chain for <paramref name="locale"/>: a supplied catalogue for the exact tag, then for each shorter tag
    /// (<c>pt-BR</c> then <c>pt</c>), then English.
    /// </summary>
    public static LocalisedText ChainFor(string locale, IEnumerable<MessageCatalog>? additional = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(locale);
        List<MessageCatalog> supplied = additional?.ToList() ?? [];
        var chain = new List<MessageCatalog>();
        string tag = locale.Trim();
        while (tag.Length > 0)
        {
            MessageCatalog? hit = supplied.Find(c => string.Equals(c.Locale, tag, StringComparison.OrdinalIgnoreCase));
            if (hit is not null && !chain.Contains(hit))
            {
                chain.Add(hit);
            }

            int cut = tag.LastIndexOf('-');
            tag = cut < 0 ? string.Empty : tag[..cut];
        }

        if (!chain.Contains(English))
        {
            chain.Add(English);
        }

        return new LocalisedText(chain);
    }

    private static MessageCatalog Load()
    {
        using Stream stream = typeof(ChromeCatalog).Assembly.GetManifestResourceStream(_englishResource)
            ?? throw new InvalidOperationException($"Missing embedded resource {_englishResource}.");
        return FromJson("en", stream);
    }
}
