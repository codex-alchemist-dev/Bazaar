using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Bazaar.Server.BrowserPool.Audit;

/// <summary>Settings of the pool audit sink. Bound from <c>Bazaar:BrowserPoolAudit</c>.</summary>
public sealed record PoolAuditOptions
{
    /// <summary>The configuration section.</summary>
    public const string SectionName = "Bazaar:BrowserPoolAudit";

    /// <summary>The longest wait for one audit write, in seconds. The sink interface is synchronous, so a stuck log must not hang the pool. Default 5.</summary>
    public int WriteTimeoutSeconds { get; init; } = 5;

    /// <summary>Longest detail text kept in a row, in characters. Longer text is cut. Default 200.</summary>
    public int MaxDetailLength { get; init; } = 200;

    /// <summary>Binds the options and rejects values out of range.</summary>
    /// <exception cref="InvalidOperationException">A value is out of range.</exception>
    public static PoolAuditOptions Bind(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        IConfigurationSection section = configuration.GetSection(SectionName);
        var defaults = new PoolAuditOptions();
        var options = new PoolAuditOptions
        {
            WriteTimeoutSeconds = ReadInt(section, nameof(WriteTimeoutSeconds), defaults.WriteTimeoutSeconds),
            MaxDetailLength = ReadInt(section, nameof(MaxDetailLength), defaults.MaxDetailLength),
        };
        if (options.WriteTimeoutSeconds is < 1 or > 300)
            throw new InvalidOperationException($"{SectionName}:WriteTimeoutSeconds must be between 1 and 300.");
        if (options.MaxDetailLength is < 1 or > 1000)
            throw new InvalidOperationException($"{SectionName}:MaxDetailLength must be between 1 and 1000.");
        return options;
    }

    private static int ReadInt(IConfigurationSection section, string key, int fallback)
    {
        string? raw = section[key];
        if (raw is null)
            return fallback;
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            throw new InvalidOperationException($"{SectionName}:{key} must be a whole number.");
        return value;
    }
}
