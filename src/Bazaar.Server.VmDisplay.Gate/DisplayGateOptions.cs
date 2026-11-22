using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Bazaar.Server.VmDisplay.Gate;

/// <summary>What the gate does with input while the agent's sessions carry hard taint. Setting <c>security.screen.input_hard_taint</c>.</summary>
public enum InputUnderHardTaint
{
    /// <summary>Allow own-VM and anonymous-browser input; refuse account browsers. Setting value <c>account-browsers</c>. The default.</summary>
    AccountBrowsers,

    /// <summary>Ask <see cref="IDisplayInputApproval"/>; refuse when none is registered or it declines. Setting value <c>approval</c>.</summary>
    Approval,

    /// <summary>Refuse all input. Setting value <c>all</c>.</summary>
    All,
}

/// <summary>Settings of the display gate. Bound from <c>Bazaar:VmDisplayGate</c>.</summary>
public sealed record DisplayGateOptions
{
    /// <summary>The configuration section.</summary>
    public const string SectionName = "Bazaar:VmDisplayGate";

    /// <summary>
    /// Whether the gate makes real decisions. Default true: agent sessions are registered in the session directory (PR #1050), so
    /// the taint reader sees the agent's live sessions. Setting it to false is the tightening direction: the gate then refuses
    /// every capture and every input, and never allows anything.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Input under hard taint (setting <c>security.screen.input_hard_taint</c>). Default <see cref="InputUnderHardTaint.AccountBrowsers"/>.</summary>
    public InputUnderHardTaint InputUnderHardTaint { get; init; } = InputUnderHardTaint.AccountBrowsers;

    /// <summary>The longest wait for one security read, in seconds. Range 1 to 60. Default 5.</summary>
    public int ReadTimeoutSeconds { get; init; } = 5;

    /// <summary>Binds the options and rejects unknown or out-of-range values; never falls back to a looser value.</summary>
    /// <exception cref="InvalidOperationException">A value is unknown or out of range.</exception>
    public static DisplayGateOptions Bind(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        IConfigurationSection section = configuration.GetSection(SectionName);
        var defaults = new DisplayGateOptions();

        bool enabled = defaults.Enabled;
        if (section[nameof(Enabled)] is { } rawEnabled && !bool.TryParse(rawEnabled, out enabled))
            throw new InvalidOperationException($"{SectionName}:{nameof(Enabled)} must be true or false.");

        InputUnderHardTaint mode = defaults.InputUnderHardTaint;
        if (section[nameof(InputUnderHardTaint)] is { } rawMode)
        {
            mode = rawMode switch
            {
                "account-browsers" => InputUnderHardTaint.AccountBrowsers,
                "approval" => InputUnderHardTaint.Approval,
                "all" => InputUnderHardTaint.All,
                _ => throw new InvalidOperationException($"{SectionName}:{nameof(InputUnderHardTaint)} must be account-browsers, approval or all."),
            };
        }

        int timeout = defaults.ReadTimeoutSeconds;
        if (section[nameof(ReadTimeoutSeconds)] is { } rawTimeout
            && !int.TryParse(rawTimeout, NumberStyles.Integer, CultureInfo.InvariantCulture, out timeout))
            throw new InvalidOperationException($"{SectionName}:{nameof(ReadTimeoutSeconds)} must be a whole number.");

        var options = new DisplayGateOptions { Enabled = enabled, InputUnderHardTaint = mode, ReadTimeoutSeconds = timeout };
        options.Validate();
        return options;
    }

    /// <summary>Checks the values.</summary>
    /// <exception cref="InvalidOperationException">A value is unknown or out of range.</exception>
    public void Validate()
    {
        if (!Enum.IsDefined(InputUnderHardTaint))
            throw new InvalidOperationException($"{SectionName}:{nameof(InputUnderHardTaint)} must be account-browsers, approval or all.");
        if (ReadTimeoutSeconds is < 1 or > 60)
            throw new InvalidOperationException($"{SectionName}:{nameof(ReadTimeoutSeconds)} must be between 1 and 60.");
    }
}
