using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Bazaar.Server.VmDensity.Vms;

/// <summary>Settings of the density VM adapter. Bound from <c>Bazaar:VmDensityVms</c> (PROPOSED, doc 59 section 8).</summary>
public sealed record VmDensityVmsOptions
{
    /// <summary>The configuration section.</summary>
    public const string SectionName = "Bazaar:VmDensityVms";

    /// <summary>
    /// The longest wait, in seconds, for the VM directory read and for each activity read. A directory read that takes longer fails the
    /// listing (the governor then admits the start untracked); an activity read that takes longer makes that VM busy. Default 5, 1 to 60.
    /// </summary>
    public int ReadTimeoutSeconds { get; init; } = 5;

    /// <summary>Binds the options and rejects values out of range.</summary>
    /// <exception cref="InvalidOperationException">A value is not usable.</exception>
    public static VmDensityVmsOptions Bind(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        string? raw = configuration.GetSection(SectionName)[nameof(ReadTimeoutSeconds)];
        int timeout = new VmDensityVmsOptions().ReadTimeoutSeconds;
        if (raw is not null && !int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out timeout))
            throw new InvalidOperationException($"{SectionName}:{nameof(ReadTimeoutSeconds)} must be a whole number.");
        if (timeout is < 1 or > 60)
            throw new InvalidOperationException($"{SectionName}:{nameof(ReadTimeoutSeconds)} must be between 1 and 60.");
        return new VmDensityVmsOptions { ReadTimeoutSeconds = timeout };
    }
}
