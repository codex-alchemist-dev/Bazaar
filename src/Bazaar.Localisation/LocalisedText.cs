using System.Text;

namespace Bazaar.Localisation;

/// <summary>
/// One locale's messages, keyed by stable message ids such as <c>tour.chat.title</c>. Immutable. Values may hold
/// <c>{name}</c> placeholders, which <see cref="LocalisedText.Resolve"/> fills from named arguments.
/// </summary>
public sealed class MessageCatalog
{
    private readonly IReadOnlyDictionary<string, string> _messages;

    private MessageCatalog(string locale, IReadOnlyDictionary<string, string> messages)
    {
        Locale = locale;
        _messages = messages;
    }

    /// <summary>BCP 47 tag, for example <c>en</c> or <c>pt-BR</c>.</summary>
    public string Locale { get; }

    public IReadOnlyCollection<string> Keys => _messages.Keys.ToList();

    /// <summary>Creates a catalogue. Throws for a blank locale, a blank key, or an empty value.</summary>
    public static MessageCatalog Create(string locale, IReadOnlyDictionary<string, string> messages)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(locale);
        ArgumentNullException.ThrowIfNull(messages);

        var copy = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string key, string value) in messages)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            copy[key] = value;
        }

        return new MessageCatalog(locale, copy);
    }

    public bool TryGet(string key, out string value) => _messages.TryGetValue(key, out value!);
}

/// <summary>
/// Looks messages up through a fallback chain: the person's locale first, then each less specific locale, then the
/// default catalogue (<c>en</c> in shipped builds). A missing key is reported as its own id in angle brackets, so a
/// gap is visible on screen and in tests instead of silently blank. Pure; heads own the locale choice.
/// </summary>
public sealed class LocalisedText
{
    private readonly List<MessageCatalog> _chain;

    /// <summary>Builds a chain. The first catalogue is tried first; the last is the fallback of last resort.</summary>
    public LocalisedText(IEnumerable<MessageCatalog> chain)
    {
        ArgumentNullException.ThrowIfNull(chain);
        var list = chain.ToList();
        if (list.Count == 0)
        {
            throw new ArgumentException("A fallback chain needs at least one catalogue.", nameof(chain));
        }

        _chain = list;
    }

    /// <summary>The locale tags in lookup order, for display in settings.</summary>
    public IReadOnlyList<string> Locales => _chain.Select(c => c.Locale).ToList();

    /// <summary>
    /// Resolves a key and fills <c>{name}</c> placeholders from <paramref name="args"/>. Unknown placeholders are left
    /// as written so a translator's typo shows up on screen. Returns <c>&lt;key&gt;</c> when no catalogue has the key.
    /// </summary>
    public string Resolve(string key, IReadOnlyDictionary<string, string>? args = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        foreach (MessageCatalog catalog in _chain)
        {
            if (catalog.TryGet(key, out string template))
            {
                return Fill(template, args);
            }
        }

        return $"<{key}>";
    }

    /// <summary>
    /// Resolves a key as an ICU message (plurals, gender select, numbers in the locale of the catalogue that has it).
    /// Returns <c>&lt;key&gt;</c> when no catalogue has the key, and <c>&lt;key: invalid&gt;</c> when the template is
    /// malformed or an argument is missing, so a translator's mistake shows on screen instead of crashing the view.
    /// </summary>
    public string ResolveIcu(string key, IReadOnlyDictionary<string, object?> args)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(args);

        foreach (MessageCatalog catalog in _chain)
        {
            if (catalog.TryGet(key, out string template))
            {
                try
                {
                    return IcuMessage.Format(template, catalog.Locale, args);
                }
                catch (FormatException)
                {
                    return $"<{key}: invalid>";
                }
            }
        }

        return $"<{key}>";
    }

    /// <summary>True when the first catalogue that has the key is not the first locale in the chain.</summary>
    public bool UsesFallback(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        for (int i = 0; i < _chain.Count; i++)
        {
            if (_chain[i].TryGet(key, out _))
            {
                return i > 0;
            }
        }

        return false;
    }

    private static string Fill(string template, IReadOnlyDictionary<string, string>? args)
    {
        if (args is null || !template.Contains('{', StringComparison.Ordinal))
        {
            return template;
        }

        var sb = new StringBuilder(template.Length);
        int i = 0;
        while (i < template.Length)
        {
            char c = template[i];
            if (c == '{')
            {
                int close = template.IndexOf('}', i + 1);
                if (close > i + 1)
                {
                    string name = template[(i + 1)..close];
                    if (args.TryGetValue(name, out string? value))
                    {
                        sb.Append(value);
                        i = close + 1;
                        continue;
                    }
                }
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }
}
