using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Bazaar.Server.VmDisplay.HyperV;

/// <summary>Hyper-V display settings, bound from the <c>Bazaar:VmDisplay:HyperV</c> section.</summary>
public sealed class HyperVOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "Bazaar:VmDisplay:HyperV";

    /// <summary>Whether this adapter serves any target.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The PowerShell executable (<c>powershell.exe</c> or <c>pwsh</c>).</summary>
    public string PowerShellPath { get; set; } = "powershell.exe";

    /// <summary>Widest thumbnail asked for.</summary>
    public int ThumbnailWidth { get; set; } = 1280;

    /// <summary>Tallest thumbnail asked for.</summary>
    public int ThumbnailHeight { get; set; } = 800;

    /// <summary>Timeout for one PowerShell run, in seconds.</summary>
    public int CommandTimeoutSeconds { get; set; } = 15;

    /// <summary>Throws <see cref="ArgumentOutOfRangeException"/> or <see cref="ArgumentException"/> when a value is not usable.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(PowerShellPath))
            throw new ArgumentException("The value must not be empty.", nameof(PowerShellPath));
        InRange(ThumbnailWidth, nameof(ThumbnailWidth));
        InRange(ThumbnailHeight, nameof(ThumbnailHeight));
        if (CommandTimeoutSeconds < 1)
            throw new ArgumentOutOfRangeException(nameof(CommandTimeoutSeconds), CommandTimeoutSeconds, "The value must be at least 1.");
    }

    /// <summary>Reads the options from <paramref name="configuration"/> and validates them.</summary>
    public static HyperVOptions Bind(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        IConfigurationSection s = configuration.GetSection(SectionName);
        var o = new HyperVOptions();
        if (bool.TryParse(s[nameof(Enabled)], out bool enabled))
            o.Enabled = enabled;
        o.PowerShellPath = s[nameof(PowerShellPath)] ?? o.PowerShellPath;
        o.ThumbnailWidth = ReadInt(s, nameof(ThumbnailWidth), o.ThumbnailWidth);
        o.ThumbnailHeight = ReadInt(s, nameof(ThumbnailHeight), o.ThumbnailHeight);
        o.CommandTimeoutSeconds = ReadInt(s, nameof(CommandTimeoutSeconds), o.CommandTimeoutSeconds);
        o.Validate();
        return o;
    }

    private static int ReadInt(IConfigurationSection section, string key, int fallback) =>
        int.TryParse(section[key], NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;

    private static void InRange(int value, string name)
    {
        // The WMI method takes uint16 sizes.
        if (value is < 1 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(name, value, "The value must be between 1 and 65535.");
    }
}
