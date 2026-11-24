using Bazaar.Server.WorkLife;

namespace Bazaar.Server.Presence.Postgres;

/// <summary>
/// Maps the presence enums to the text the <c>agent_activity</c> CHECK constraints accept (doc 04: enum columns are text). The
/// names are fixed snake-case words, not <c>ToString()</c>, so renaming a C# member never changes stored data.
/// </summary>
internal static class PresenceColumnCodec
{
    private static readonly (PresenceOverlay Flag, string Text)[] _overlays =
    [
        (PresenceOverlay.OnBreak, "on_break"),
        (PresenceOverlay.WaitingForApproval, "waiting_for_approval"),
        (PresenceOverlay.Throttled, "throttled"),
        (PresenceOverlay.Covered, "covered"),
        (PresenceOverlay.Ward, "ward"),
        (PresenceOverlay.Idle, "idle"),
    ];

    public const string NoOverlay = "none";

    public static string Availability(PresenceAvailability value) => value switch
    {
        PresenceAvailability.OnShift => "on_shift",
        PresenceAvailability.Draining => "draining",
        PresenceAvailability.Break => "break",
        PresenceAvailability.Off => "off",
        PresenceAvailability.Vacation => "vacation",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown availability."),
    };

    public static PresenceAvailability ParseAvailability(string text) => text switch
    {
        "on_shift" => PresenceAvailability.OnShift,
        "draining" => PresenceAvailability.Draining,
        "break" => PresenceAvailability.Break,
        "off" => PresenceAvailability.Off,
        "vacation" => PresenceAvailability.Vacation,
        _ => throw new FormatException($"Unknown availability '{text}'."),
    };

    public static string Activity(PresenceActivityKind value) => value switch
    {
        PresenceActivityKind.Idle => "idle",
        PresenceActivityKind.ModelCall => "model_call",
        PresenceActivityKind.Tool => "tool",
        PresenceActivityKind.Waiting => "waiting",
        PresenceActivityKind.Drafting => "drafting",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown activity kind."),
    };

    public static PresenceActivityKind ParseActivity(string text) => text switch
    {
        "idle" => PresenceActivityKind.Idle,
        "model_call" => PresenceActivityKind.ModelCall,
        "tool" => PresenceActivityKind.Tool,
        "waiting" => PresenceActivityKind.Waiting,
        "drafting" => PresenceActivityKind.Drafting,
        _ => throw new FormatException($"Unknown activity kind '{text}'."),
    };

    /// <summary>The overlay flags as a comma-separated list in a fixed order, or <see cref="NoOverlay"/>.</summary>
    public static string Overlay(PresenceOverlay value)
    {
        var names = _overlays.Where(o => value.HasFlag(o.Flag)).Select(o => o.Text).ToArray();
        return names.Length == 0 ? NoOverlay : string.Join(',', names);
    }

    public static PresenceOverlay ParseOverlay(string text)
    {
        if (text == NoOverlay)
        {
            return PresenceOverlay.None;
        }

        var result = PresenceOverlay.None;
        foreach (var part in text.Split(','))
        {
            var match = Array.Find(_overlays, o => o.Text == part);
            if (match.Text is null)
            {
                throw new FormatException($"Unknown overlay '{part}'.");
            }

            result |= match.Flag;
        }

        return result;
    }
}
