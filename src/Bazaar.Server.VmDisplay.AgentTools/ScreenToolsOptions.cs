using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Bazaar.Server.VmDisplay.AgentTools;

/// <summary>
/// Caps for the screen tools, bound from the <c>Bazaar:VmDisplay:AgentTools</c> section. Values may tighten the hard ceilings below,
/// never loosen them. The display module's own limits (rate, frame size, action count) apply in addition.
/// </summary>
public sealed class ScreenToolsOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "Bazaar:VmDisplay:AgentTools";

    /// <summary>Most actions one <c>screen.input</c> call may carry, whatever the setting says.</summary>
    public const int HardMaxActions = 50;

    /// <summary>Largest image, in bytes, a setting may allow in a tool result.</summary>
    public const int HardMaxImageBytes = 8 * 1024 * 1024;

    /// <summary>Longest text one <c>type_text</c> action may carry, whatever the setting says.</summary>
    public const int HardMaxTextLength = 2000;

    /// <summary>Largest PNG returned inside a tool result (as base64). A larger frame is refused.</summary>
    public int MaxImageBytes { get; set; } = 1024 * 1024;

    /// <summary>Most actions in one <c>screen.input</c> call.</summary>
    public int MaxActionsPerCall { get; set; } = 20;

    /// <summary>Longest text in one <c>type_text</c> action, in UTF-16 code units.</summary>
    public int MaxTextLength { get; set; } = 500;

    /// <summary>Throws <see cref="ArgumentOutOfRangeException"/> when a value is not usable or loosens a hard ceiling.</summary>
    public void Validate()
    {
        Within(MaxImageBytes, HardMaxImageBytes, nameof(MaxImageBytes));
        Within(MaxActionsPerCall, HardMaxActions, nameof(MaxActionsPerCall));
        Within(MaxTextLength, HardMaxTextLength, nameof(MaxTextLength));
    }

    /// <summary>Reads the options from <paramref name="configuration"/> (the <see cref="SectionName"/> section) and validates them.</summary>
    public static ScreenToolsOptions Bind(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        IConfigurationSection s = configuration.GetSection(SectionName);
        var o = new ScreenToolsOptions();
        o.MaxImageBytes = ReadInt(s, nameof(MaxImageBytes), o.MaxImageBytes);
        o.MaxActionsPerCall = ReadInt(s, nameof(MaxActionsPerCall), o.MaxActionsPerCall);
        o.MaxTextLength = ReadInt(s, nameof(MaxTextLength), o.MaxTextLength);
        o.Validate();
        return o;
    }

    private static int ReadInt(IConfigurationSection section, string key, int fallback) =>
        int.TryParse(section[key], NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;

    private static void Within(int value, int ceiling, string name)
    {
        if (value < 1 || value > ceiling)
            throw new ArgumentOutOfRangeException(name, value, $"The value must be 1 to {ceiling}.");
    }
}
