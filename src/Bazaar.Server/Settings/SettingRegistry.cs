namespace Bazaar.Server.Settings;

/// <summary>The set of settings this build knows. Unknown keys are rejected, so a stored row without a key here is never read.</summary>
public sealed class SettingRegistry
{
    private readonly Dictionary<string, SettingDefinition> _byName = new(StringComparer.Ordinal);

    public SettingRegistry(IEnumerable<SettingDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        foreach (var definition in definitions)
        {
            ArgumentNullException.ThrowIfNull(definition);
            if (!_byName.TryAdd(definition.Name, definition))
                throw new ArgumentException($"Setting '{definition.Name}' is registered twice.", nameof(definitions));
        }
    }

    /// <summary>Every <c>host.*</c> key from doc 03.</summary>
    public static SettingRegistry Host { get; } = new(HostSettings.All);

    /// <summary>Every setting this build knows: the host keys and the calendar keys (doc 24). The store still reads <see cref="Host"/> until it is wired to this set.</summary>
    public static SettingRegistry Known { get; } = new(HostSettings.All.Concat(Calendar.CalendarSettings.All));

    /// <summary>Every <c>constellation.*</c> key from doc 19. Kept apart from <see cref="Host"/>, which holds only <c>host.*</c>.</summary>
    public static SettingRegistry Constellation { get; } = new(ConstellationSettings.All);

    public IReadOnlyCollection<SettingDefinition> Definitions => _byName.Values;

    public bool TryGet(string name, out SettingDefinition? definition)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _byName.TryGetValue(name, out definition);
    }

    /// <summary>True when this exact key object is registered (same name and same instance).</summary>
    public bool Contains(SettingDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return _byName.TryGetValue(definition.Name, out var registered) && ReferenceEquals(registered, definition);
    }
}
