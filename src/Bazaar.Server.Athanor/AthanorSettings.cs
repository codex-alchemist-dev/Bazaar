using Bazaar.Server.Settings;

namespace Bazaar.Server.Athanor;

/// <summary>Default limits (doc 57 section 6), also the plain implementation of <see cref="IAthanorLimits"/>.</summary>
public sealed class AthanorLimits : IAthanorLimits
{
    public const long DefaultMaxBytes = 5L * 1024 * 1024;
    public const int DefaultSaveRatePerMinute = 60;

    public long MaxOpenBytes { get; init; } = DefaultMaxBytes;

    public long MaxSaveBytes { get; init; } = DefaultMaxBytes;

    public int SaveRatePerMinute { get; init; } = DefaultSaveRatePerMinute;
}

/// <summary>The Athanor operator settings (doc 57 section 6). Install scope, operator-changed, safe defaults.</summary>
public static class AthanorSettings
{
    public static readonly SettingKey<int> MaxOpenBytes = new(
        "athanor.max_open_bytes", (int)AthanorLimits.DefaultMaxBytes, SettingScope.Install, SettingRole.Operator,
        "Largest file the editor opens, in bytes.",
        validate: AtLeast1("athanor.max_open_bytes"));

    public static readonly SettingKey<int> MaxSaveBytes = new(
        "athanor.max_save_bytes", (int)AthanorLimits.DefaultMaxBytes, SettingScope.Install, SettingRole.Operator,
        "Largest file the editor saves, in bytes.",
        validate: AtLeast1("athanor.max_save_bytes"));

    public static readonly SettingKey<int> SaveRatePerMinute = new(
        "athanor.save_rate_per_min", AthanorLimits.DefaultSaveRatePerMinute, SettingScope.Install, SettingRole.Operator,
        "Editor saves allowed per user per minute.",
        validate: AtLeast1("athanor.save_rate_per_min"));

    public static IReadOnlyList<SettingDefinition> All { get; } = [MaxOpenBytes, MaxSaveBytes, SaveRatePerMinute];

    private static Func<int, string?> AtLeast1(string name) =>
        value => value >= 1 ? null : $"'{name}' must be at least 1.";
}

/// <summary>Athanor's contribution to the settings registry.</summary>
public sealed class AthanorSettingContribution : ISettingContribution
{
    public IReadOnlyList<SettingDefinition> Settings => AthanorSettings.All;
}
