using Bazaar.Client.Settings;

namespace Bazaar.Client.PhoneCoach;

/// <summary>
/// The first-run coach's state: shown once, then never again until the person replays it from settings. Whether it has been
/// seen is the client setting <see cref="CoachSettingKeys.Seen"/>, so it survives restarts with the rest of the client settings.
/// </summary>
public sealed class CoachController
{
    private readonly IClientSettings _settings;
    private int _step;
    private bool _replaying;

    /// <summary>The setting definitions this module needs registered with the head's <see cref="IClientSettings"/> store.</summary>
    public static IReadOnlyList<ClientSettingDefinition> Definitions { get; } =
    [
        new(CoachSettingKeys.Seen, "false", ["true", "false"]),
    ];

    public CoachController(IClientSettings settings, IReadOnlyList<CoachStep>? steps = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        Steps = steps is { Count: > 0 } ? steps : CoachStep.Default;
        IsShowing = !Seen;
    }

    /// <summary>The prompts, in order.</summary>
    public IReadOnlyList<CoachStep> Steps { get; }

    /// <summary>True once the person has dismissed or finished the coach on this device.</summary>
    public bool Seen => string.Equals(_settings.GetValue(CoachSettingKeys.Seen), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>True while the overlay should be on screen.</summary>
    public bool IsShowing { get; private set; }

    /// <summary>Index into <see cref="Steps"/> of the prompt on screen.</summary>
    public int StepIndex => _step;

    /// <summary>The prompt on screen, or null when the coach is not showing.</summary>
    public CoachStep? CurrentStep => IsShowing ? Steps[_step] : null;

    /// <summary>True on the last prompt, where the next button becomes "Got it".</summary>
    public bool IsLastStep => _step == Steps.Count - 1;

    /// <summary>Raised after <see cref="IsShowing"/> or <see cref="StepIndex"/> changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Moves to the next prompt; on the last one it finishes (same as <see cref="Dismiss"/>). Returns false when not showing.</summary>
    public bool Next()
    {
        if (!IsShowing)
        {
            return false;
        }

        if (IsLastStep)
        {
            Dismiss();
        }
        else
        {
            _step++;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return true;
    }

    /// <summary>Closes the coach from any prompt and records it as seen, unless it is a replay (which leaves the flag as it was).</summary>
    public void Dismiss()
    {
        if (!IsShowing)
        {
            return;
        }

        IsShowing = false;
        _step = 0;
        if (!_replaying)
        {
            _settings.TrySetValue(CoachSettingKeys.Seen, "true");
        }

        _replaying = false;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Shows the coach again from the first prompt (the settings entry). Does not clear the seen flag.</summary>
    public void Replay()
    {
        _replaying = Seen;
        _step = 0;
        IsShowing = true;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
