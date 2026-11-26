using Bazaar.Server.AgentSafety.Anomaly;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.AgentSafety.AutoContainment;

/// <summary>
/// Operator settings for automatic containment (doc 48 section 5.6). Every value has a safe default. The undo window has a
/// floor that a setting may not go below (settings tighten, never loosen below the floor); global containment is not a setting
/// at all, because it needs the owner's role.
/// </summary>
public sealed class AutoContainmentOptions
{
    /// <summary>The smallest undo window an operator may configure.</summary>
    public static readonly TimeSpan MinimumUndoWindow = TimeSpan.FromMinutes(1);

    /// <summary>Alerts at or above this severity contain the one agent they are about. Default <see cref="AnomalySeverity.High"/>.</summary>
    public AnomalySeverity Threshold { get; set; } = AnomalySeverity.High;

    /// <summary>How long after containment the owner may undo it with a signed release. Default 15 minutes; never below <see cref="MinimumUndoWindow"/>.</summary>
    public TimeSpan UndoWindow { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>How many times a failing containment is tried before the record shows it failed. Between 1 and 10; default 3.</summary>
    public int MaxContainAttempts { get; set; } = 3;

    /// <summary>Delay before the second attempt; it doubles for each further attempt. Positive; default 1 second.</summary>
    public TimeSpan RetryBackoff { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>How often elapsed undo windows are marked expired. Positive; default 30 seconds.</summary>
    public TimeSpan ExpirySweepInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Returns the first problem with these values, or null when they are valid.</summary>
    public string? Validate()
    {
        if (!Enum.IsDefined(Threshold))
        {
            return "Threshold is not a known severity.";
        }
        if (UndoWindow < MinimumUndoWindow)
        {
            return $"UndoWindow must be at least {MinimumUndoWindow}.";
        }
        if (MaxContainAttempts is < 1 or > 10)
        {
            return "MaxContainAttempts must be between 1 and 10.";
        }
        if (RetryBackoff <= TimeSpan.Zero)
        {
            return "RetryBackoff must be positive.";
        }
        if (ExpirySweepInterval <= TimeSpan.Zero)
        {
            return "ExpirySweepInterval must be positive.";
        }
        return null;
    }
}

/// <summary>Rejects <see cref="AutoContainmentOptions"/> values below their floors.</summary>
public sealed class AutoContainmentOptionsValidator : IValidateOptions<AutoContainmentOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, AutoContainmentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var problem = options.Validate();
        return problem is null ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(problem);
    }
}
