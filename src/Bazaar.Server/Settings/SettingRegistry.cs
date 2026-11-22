namespace Bazaar.Server.Settings;

/// <summary>
/// The built-in registry composition. <see cref="SettingRegistry"/> itself lives in Bazaar.Server.Settings; the keys of the
/// features that still live in Bazaar.Server (calendar, gc, knowledge) are gathered here, so call sites keep
/// writing <c>SettingRegistry.Known</c> and <c>SettingRegistry.WithContributions(...)</c>.
/// </summary>
public static class SettingRegistryComposition
{
    private static readonly SettingRegistry _known = new(BuiltInSettings);

    private static IEnumerable<SettingDefinition> BuiltInSettings =>
        HostSettings.All
            .Concat(Calendar.CalendarSettings.All)
            .Concat(Gc.GcSettings.All)
            .Concat(Knowledge.KnowledgeSettings.All)
            .Concat(ConstellationSettings.All)
            .Concat(SecuritySettings.All);

    extension(SettingRegistry)
    {
        /// <summary>
        /// Every built-in setting: the host keys, the calendar keys (doc 24), the knowledge keys (doc 25), the
        /// constellation keys (doc 19), the gateway keys (doc 05) and the doc 15 security keys. Module keys (the module manager's,
        /// doc 42 and doc 49) arrive as contributions through <c>WithContributions</c>, so the host composes them.
        /// </summary>
        public static SettingRegistry Known => _known;

        /// <summary>
        /// The built-in keys plus every module contribution. A contributed name that repeats a built-in or another
        /// contribution is rejected, as a duplicate in the constructor. The host builds its registry this way.
        /// </summary>
        public static SettingRegistry WithContributions(IEnumerable<ISettingContribution> contributions)
        {
            ArgumentNullException.ThrowIfNull(contributions);
            return new(BuiltInSettings.Concat(contributions.SelectMany(c => c.Settings)));
        }
    }
}
