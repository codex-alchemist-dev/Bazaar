namespace Bazaar.Server.Modules.Updates;

/// <summary>
/// The module manager setting key and shipped default that other assemblies (the Server settings registry) need without
/// referencing the implementation. <see cref="DeferAging"/> keeps public aliases of both.
/// </summary>
public static class ModuleManagerSettingNames
{
    /// <summary>The setting key that holds the warning threshold in days. Read by the caller, not here.</summary>
    public const string DeferWarnDaysSettingKey = "deps.update.deferWarnDays";

    /// <summary>The shipped default for <see cref="DeferWarnDaysSettingKey"/>.</summary>
    public const int DefaultDeferWarnDays = 60;
}
