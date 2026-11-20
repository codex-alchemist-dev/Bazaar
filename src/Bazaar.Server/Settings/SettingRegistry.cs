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

    /// <summary>
    /// Every built-in setting: the host keys, the calendar keys (doc 24), the knowledge keys (doc 25), the
    /// constellation keys (doc 19) and the doc 15 security keys. Module keys (the module manager's, doc 42 and doc 49)
    /// arrive as contributions through <see cref="WithContributions"/>. The gateway keys (doc 05) live in Bazaar.Server.Gateway,
    /// which this project cannot reference; the host adds them with <see cref="With"/>.
    /// </summary>
    public static SettingRegistry Known { get; } = new(BuiltInSettings);

    private static IEnumerable<SettingDefinition> BuiltInSettings =>
        HostSettings.All
            .Concat(Calendar.CalendarSettings.All)
            .Concat(Gc.GcSettings.All)
            .Concat(Knowledge.KnowledgeSettings.All)
            .Concat(ConstellationSettings.All)
            .Concat(SecuritySettings.All);

    /// <summary>
    /// The built-in keys plus every module contribution. A contributed name that repeats a built-in or another
    /// contribution is rejected, as a duplicate in the constructor. The host builds its registry this way.
    /// </summary>
    public static SettingRegistry WithContributions(IEnumerable<ISettingContribution> contributions)
    {
        ArgumentNullException.ThrowIfNull(contributions);
        return new(BuiltInSettings.Concat(contributions.SelectMany(c => c.Settings)));
    }

    /// <summary>Every <c>constellation.*</c> key from doc 19. Kept apart from <see cref="Host"/>, which holds only <c>host.*</c>.</summary>
    public static SettingRegistry Constellation { get; } = new(ConstellationSettings.All);

    public IReadOnlyCollection<SettingDefinition> Definitions => _byName.Values;

    /// <summary>
    /// This registry plus <paramref name="additional"/>. The host uses it to add definitions from modules this project does not
    /// reference, for example <c>SettingRegistry.Known.With(GatewaySettings.All)</c>. The same instances are kept, so
    /// <see cref="Contains"/> still holds for them. A duplicate name throws, as in the constructor.
    /// </summary>
    public SettingRegistry With(IEnumerable<SettingDefinition> additional)
    {
        ArgumentNullException.ThrowIfNull(additional);
        return new([.. Definitions, .. additional]);
    }

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
