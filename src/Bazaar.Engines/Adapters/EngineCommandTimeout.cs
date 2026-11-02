namespace Bazaar.Engines.Adapters;

/// <summary>
/// The <c>engine.command.timeout_minutes</c> setting (docs/design/20-engine-integration-api.md, Settings): an integer
/// from 1 to 120 minutes, default 15. 120 is doc 16's hard maximum. Range checks happen here, when the setting is
/// converted, so <see cref="AdapterHost"/> takes a plain <see cref="TimeSpan"/> and tests can use short timeouts.
/// </summary>
public static class EngineCommandTimeout
{
    /// <summary>The settings key, as registered in the settings registry (doc 03).</summary>
    public const string SettingKey = "engine.command.timeout_minutes";

    public const int MinMinutes = 1;
    public const int MaxMinutes = 120;
    public const int DefaultMinutes = 15;

    /// <summary>Converts a whole-minute setting value to a timeout, rejecting values outside 1 to 120.</summary>
    public static TimeSpan FromMinutes(int minutes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(minutes, MinMinutes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minutes, MaxMinutes);
        return TimeSpan.FromMinutes(minutes);
    }

    /// <summary>The shipped default: 15 minutes.</summary>
    public static TimeSpan Default => FromMinutes(DefaultMinutes);
}
