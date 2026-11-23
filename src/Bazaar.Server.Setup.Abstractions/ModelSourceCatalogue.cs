using System.Collections.Frozen;

namespace Bazaar.Server.Setup;

/// <summary>
/// One entry of the default model source catalogue (doc 05, "Default catalogue"): what the gateway needs to store a source
/// (<c>gw_provider</c> columns) that a setup plan's <c>{id, keyRef}</c> does not carry.
/// </summary>
/// <param name="Id">The provider id written to <c>gw_provider.id</c>, for example <c>anthropic-direct</c>.</param>
/// <param name="Kind">The <c>gw_provider.kind</c>: first_party, aggregator, gateway, reverse_proxy or local.</param>
/// <param name="Adapter">The <c>gw_provider.adapter</c>: openai_chat, anthropic_messages or gemini_generate_content.</param>
/// <param name="BaseUrl">The base URL the doc 05 catalogue states for the source.</param>
/// <param name="AuthKind">The credential kind: api_key or bearer when the source takes a key, none for a local runtime. Never a subscription login.</param>
/// <param name="Aliases">Other plan ids that name this source (doc 45's example calls the owner's Anthropic key <c>anthropic-owner</c>).</param>
public sealed record ModelSourceEntry(string Id, string Kind, string Adapter, string BaseUrl, string AuthKind, IReadOnlyList<string> Aliases)
{
    /// <summary>True when the source stores a key reference (every kind but none).</summary>
    public bool TakesKey => !string.Equals(AuthKind, "none", StringComparison.Ordinal);
}

/// <summary>Finds the gateway facts for a plan source id. The shipped catalogue is <see cref="ModelSourceCatalogue.Default"/>; a host may supply its own.</summary>
public interface IModelSourceCatalogue
{
    /// <summary>Every entry, in no promised order.</summary>
    IReadOnlyCollection<ModelSourceEntry> Entries { get; }

    /// <summary>Finds an entry by its id or one of its aliases. Exact, case-sensitive match.</summary>
    bool TryGet(string planId, out ModelSourceEntry? entry);
}

/// <summary>
/// A catalogue built from a fixed entry list. The default one carries only sources whose base URL doc 05 states. A source doc 05
/// marks "to confirm", "operator-set" or "account-specific" is left out on purpose: a plan naming it is refused with a message, because
/// guessing a base URL would be inventing data. An operator adds such a source in the admin panel (doc 05, add-provider form).
/// </summary>
public sealed class ModelSourceCatalogue : IModelSourceCatalogue
{
    private readonly FrozenDictionary<string, ModelSourceEntry> _byName;

    /// <summary>Builds a catalogue. An id or alias listed twice throws, so a typo cannot silently shadow an entry.</summary>
    public ModelSourceCatalogue(IEnumerable<ModelSourceEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var list = entries.ToList();
        var byName = new Dictionary<string, ModelSourceEntry>(StringComparer.Ordinal);
        foreach (var entry in list)
        {
            foreach (var name in entry.Aliases.Prepend(entry.Id))
            {
                if (!byName.TryAdd(name, entry))
                {
                    throw new ArgumentException($"Model source name '{name}' is listed twice.", nameof(entries));
                }
            }
        }

        Entries = list;
        _byName = byName.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <summary>The shipped default catalogue (doc 05). Free tiers, terms risk and training flags are the operator's to read; this holds the facts a row needs.</summary>
    public static ModelSourceCatalogue Default { get; } = new(
    [
        new("anthropic-direct", "first_party", "anthropic_messages", "https://api.anthropic.com", "api_key", ["anthropic", "anthropic-owner"]),
        new("gemini", "first_party", "gemini_generate_content", "https://generativelanguage.googleapis.com/v1beta", "api_key", ["google-gemini"]),
        new("openai", "first_party", "openai_chat", "https://api.openai.com/v1", "bearer", []),
        new("openrouter", "aggregator", "openai_chat", "https://openrouter.ai/api/v1", "bearer", []),
        new("huggingface", "aggregator", "openai_chat", "https://router.huggingface.co/v1", "bearer", ["hugging-face"]),
        // Doc 05b: the owner's base URL (127.0.0.1) wins over the localhost spelling in doc 05.
        new("freellmapi", "gateway", "openai_chat", "http://127.0.0.1:3001/v1", "bearer", []),
        new("ollama", "local", "openai_chat", "http://localhost:11434/v1", "none", []),
        new("lm-studio", "local", "openai_chat", "http://127.0.0.1:1234/v1", "none", ["lmstudio"]),
    ]);

    /// <inheritdoc />
    public IReadOnlyCollection<ModelSourceEntry> Entries { get; }

    /// <inheritdoc />
    public bool TryGet(string planId, out ModelSourceEntry? entry)
    {
        if (planId is not null && _byName.TryGetValue(planId, out var found))
        {
            entry = found;
            return true;
        }

        entry = null;
        return false;
    }
}
