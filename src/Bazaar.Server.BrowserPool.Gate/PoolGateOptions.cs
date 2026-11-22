using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Bazaar.Server.BrowserPool.Gate;

/// <summary>Settings of the pool gate. Bound from <c>Bazaar:BrowserPoolGate</c>.</summary>
public sealed record PoolGateOptions
{
    /// <summary>The configuration section.</summary>
    public const string SectionName = "Bazaar:BrowserPoolGate";

    /// <summary>The longest wait for one kill switch read, in seconds. A read that takes longer counts as blocked. Default 5.</summary>
    public int ReadTimeoutSeconds { get; init; } = 5;

    /// <summary>Binds the options and rejects values out of range.</summary>
    /// <exception cref="InvalidOperationException">A value is out of range.</exception>
    public static PoolGateOptions Bind(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        IConfigurationSection section = configuration.GetSection(SectionName);
        int timeout = new PoolGateOptions().ReadTimeoutSeconds;
        string? raw = section[nameof(ReadTimeoutSeconds)];
        if (raw is not null && !int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out timeout))
            throw new InvalidOperationException($"{SectionName}:{nameof(ReadTimeoutSeconds)} must be a whole number.");
        if (timeout is < 1 or > 60)
            throw new InvalidOperationException($"{SectionName}:{nameof(ReadTimeoutSeconds)} must be between 1 and 60.");
        return new PoolGateOptions { ReadTimeoutSeconds = timeout };
    }
}
