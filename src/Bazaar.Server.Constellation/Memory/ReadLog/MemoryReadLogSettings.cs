using Bazaar.Server.Settings;

namespace Bazaar.Server.Constellation.Memory.ReadLog;

/// <summary>
/// The owner-read log settings (docs/design/constellation-memory-graph.md, "Owner-read access log"). The cap is an operator setting
/// with a safe default; the hard cap is not a setting.
/// </summary>
public static class MemoryReadLogSettings
{
    /// <summary>The shipped default of <see cref="MaxIdsPerRowKey"/>.</summary>
    public const int MaxIdsPerRowDefault = 200;

    /// <summary>The hard cap of <see cref="MaxIdsPerRowKey"/>.</summary>
    public const int MaxIdsPerRowHardCap = 2000;

    public static readonly SettingKey<int> MaxIdsPerRowKey = new(
        "memory.read_log.max_ids_per_row", MaxIdsPerRowDefault, SettingScope.Install, SettingRole.Operator,
        $"Owner-read log: ids stored per row; a larger read keeps the first ids, the full count and a truncated flag (hard cap {MaxIdsPerRowHardCap}).",
        validate: value => value >= 1 && value <= MaxIdsPerRowHardCap ? null : $"must be between 1 and {MaxIdsPerRowHardCap}; got {value}.");

    /// <summary>Every <c>memory.read_log.*</c> key.</summary>
    public static IReadOnlyList<SettingDefinition> All { get; } = [MaxIdsPerRowKey];
}

/// <summary>Registers <see cref="MemoryReadLogSettings.All"/> with the settings registry.</summary>
public sealed class MemoryReadLogSettingContribution : ISettingContribution
{
    public IReadOnlyList<SettingDefinition> Settings => MemoryReadLogSettings.All;
}
