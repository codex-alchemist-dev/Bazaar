using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Bazaar.Server.BrowserPool.Egress;

/// <summary>Settings of the pool egress adapter. Bound from <c>Bazaar:BrowserPoolEgress</c>.</summary>
public sealed record PoolEgressOptions
{
    /// <summary>The configuration section.</summary>
    public const string SectionName = "Bazaar:BrowserPoolEgress";

    /// <summary>The longest wait for one allowlist store read, in seconds. A read that takes longer means no browser. Default 5.</summary>
    public int ReadTimeoutSeconds { get; init; } = 5;

    /// <summary>Binds the options and rejects values out of range.</summary>
    /// <exception cref="InvalidOperationException">A value is out of range.</exception>
    public static PoolEgressOptions Bind(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        IConfigurationSection section = configuration.GetSection(SectionName);
        int timeout = new PoolEgressOptions().ReadTimeoutSeconds;
        string? raw = section[nameof(ReadTimeoutSeconds)];
        if (raw is not null && !int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out timeout))
            throw new InvalidOperationException($"{SectionName}:{nameof(ReadTimeoutSeconds)} must be a whole number.");
        if (timeout is < 1 or > 60)
            throw new InvalidOperationException($"{SectionName}:{nameof(ReadTimeoutSeconds)} must be between 1 and 60.");
        return new PoolEgressOptions { ReadTimeoutSeconds = timeout };
    }
}
