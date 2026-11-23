namespace Bazaar.Server.Settings;

/// <summary>
/// The set of settings a build knows. Unknown keys are rejected, so a stored row without a key here is never read.
/// The class lives in the abstractions project so modules can use it without referencing Bazaar.Server. The built-in
/// registries (<c>Known</c>, <c>Host</c>, <c>Constellation</c>) stay in Bazaar.Server as <c>BuiltInSettingRegistries</c>.
/// </summary>
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

    /// <summary>
    /// The built-in keys plus every module contribution, in that order. A contributed name that repeats a built-in or
    /// another contribution is rejected, as a duplicate in the constructor.
    /// </summary>
    public static SettingRegistry Combine(IEnumerable<SettingDefinition> builtIn, IEnumerable<ISettingContribution> contributions)
    {
        ArgumentNullException.ThrowIfNull(builtIn);
        ArgumentNullException.ThrowIfNull(contributions);
        return new(builtIn.Concat(contributions.SelectMany(c => c.Settings)));
    }

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
