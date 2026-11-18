namespace Bazaar.Server.Settings;

/// <summary>
/// The built-in setting registries. The registry type and the host keys live in Bazaar.Server.Settings; the key sets of
/// the modules that still live in Bazaar.Server (calendar, GC, knowledge, constellation, gateway, security and the module
/// manager) are listed here, because the Settings project cannot reference Bazaar.Server.
/// </summary>
public static class BuiltInSettingRegistry
{
    /// <summary>
    /// Every setting this build knows: the host keys, the calendar keys (doc 24), the knowledge keys (doc 25), the
    /// constellation keys (doc 19), the gateway keys (doc 05), the doc 15 security keys and the module manager keys (doc 42, doc 49). The settings store reads this set, so a stored <c>constellation.*</c> row is readable.
    /// </summary>
    public static SettingRegistry Known { get; } = new(BuiltInSettings);

    /// <summary>Every <c>constellation.*</c> key from doc 19. Kept apart from <see cref="SettingRegistry.Host"/>, which holds only <c>host.*</c>.</summary>
    public static SettingRegistry Constellation { get; } = new(ConstellationSettings.All);

    /// <summary>
    /// The built-in keys plus every module contribution. A contributed name that repeats a built-in or another
    /// contribution is rejected, as a duplicate in the constructor.
    /// </summary>
    public static SettingRegistry WithContributions(IEnumerable<ISettingContribution> contributions)
    {
        ArgumentNullException.ThrowIfNull(contributions);
        return new(BuiltInSettings.Concat(contributions.SelectMany(c => c.Settings)));
    }

    private static IEnumerable<SettingDefinition> BuiltInSettings =>
        HostSettings.All
            .Concat(Calendar.CalendarSettings.All)
            .Concat(Gc.GcSettings.All)
            .Concat(Knowledge.KnowledgeSettings.All)
            .Concat(ConstellationSettings.All)
            .Concat(Gateway.GatewaySettings.All)
            .Concat(SecuritySettings.All)
            .Concat(ModuleManagerSettings.All);
}
