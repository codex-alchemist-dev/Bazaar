using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Bazaar.Server.VmDisplay.AgentTools.Vms;

/// <summary>Settings of the agent display targets adapter. Bound from <c>Bazaar:VmDisplayAgentTargets</c>.</summary>
public sealed record AgentTargetsOptions
{
    /// <summary>The configuration section.</summary>
    public const string SectionName = "Bazaar:VmDisplayAgentTargets";

    /// <summary>
    /// Whether the adapter registers <c>IAgentDisplayTargets</c> at all. Default false: without a registration the screen tools are not
    /// offered to any agent. Turning it on is the operator's decision to let agents look at and drive their own VM screens.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>The longest wait for one VM directory read, in seconds. A read that takes longer gives no target. Default 5.</summary>
    public int ReadTimeoutSeconds { get; init; } = 5;

    /// <summary>Binds the options and rejects values out of range.</summary>
    /// <exception cref="InvalidOperationException">A value is not usable.</exception>
    public static AgentTargetsOptions Bind(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        IConfigurationSection section = configuration.GetSection(SectionName);

        bool enabled = false;
        string? rawEnabled = section[nameof(Enabled)];
        if (rawEnabled is not null && !bool.TryParse(rawEnabled, out enabled))
            throw new InvalidOperationException($"{SectionName}:{nameof(Enabled)} must be true or false.");

        int timeout = new AgentTargetsOptions().ReadTimeoutSeconds;
        string? rawTimeout = section[nameof(ReadTimeoutSeconds)];
        if (rawTimeout is not null && !int.TryParse(rawTimeout, NumberStyles.Integer, CultureInfo.InvariantCulture, out timeout))
            throw new InvalidOperationException($"{SectionName}:{nameof(ReadTimeoutSeconds)} must be a whole number.");
        if (timeout is < 1 or > 60)
            throw new InvalidOperationException($"{SectionName}:{nameof(ReadTimeoutSeconds)} must be between 1 and 60.");

        return new AgentTargetsOptions { Enabled = enabled, ReadTimeoutSeconds = timeout };
    }
}
