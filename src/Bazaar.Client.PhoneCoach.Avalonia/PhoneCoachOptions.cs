namespace Bazaar.Client.PhoneCoach.Avalonia;

/// <summary>
/// The sizes the strip, the peek and the coach use (doc 93: views never carry literals). Defaults are the initial values, to be tuned on
/// devices; a theme or motion document may replace the record through the normal scopes. Colours come from theme resource keys, listed below.
/// </summary>
public sealed record PhoneCoachOptions
{
    /// <summary>Theme resource key for the strip background.</summary>
    public const string StripSurfaceToken = "Bazaar.Strip.Surface";

    /// <summary>Theme resource key for the current chat's name.</summary>
    public const string StripTextToken = "Bazaar.Strip.Text";

    /// <summary>Theme resource key for the neighbours' names and the swipe hint.</summary>
    public const string StripMutedToken = "Bazaar.Strip.TextMuted";

    /// <summary>Theme resource key for the accent: the current name's underline and the peek edge.</summary>
    public const string AccentToken = "Bazaar.Accent";

    /// <summary>Theme resource key for the coach card surface.</summary>
    public const string CoachSurfaceToken = "Bazaar.Coach.Surface";

    /// <summary>Theme resource key for the coach card text.</summary>
    public const string CoachTextToken = "Bazaar.Coach.Text";

    /// <summary>Theme resource key for the dimmed backdrop behind the coach card.</summary>
    public const string CoachScrimToken = "Bazaar.Coach.Scrim";

    /// <summary>Strip height in dp.</summary>
    public double StripHeightDp { get; init; } = 40;

    /// <summary>Width of a neighbouring page's visible edge at the left and right of the pager (doc 93: "a few pixels"), in dp.</summary>
    public double PeekWidthDp { get; init; } = 6;

    /// <summary>Opacity of the neighbours' names and the peek edge next to the current page, 0 to 1.</summary>
    public double MutedOpacity { get; init; } = 0.55;

    /// <summary>Strip text size in dp.</summary>
    public double StripFontSize { get; init; } = 15;

    /// <summary>Space between names in the strip, in dp.</summary>
    public double StripSpacingDp { get; init; } = 16;

    /// <summary>Coach card width cap in dp, so it stays a card on an unfolded screen.</summary>
    public double CoachMaxWidthDp { get; init; } = 360;

    /// <summary>Coach card inner padding in dp.</summary>
    public double CoachPaddingDp { get; init; } = 20;

    /// <summary>Coach card corner radius in dp.</summary>
    public double CoachCornerDp { get; init; } = 16;

    /// <summary>Coach title size in dp.</summary>
    public double CoachTitleSize { get; init; } = 20;

    /// <summary>Coach body size in dp.</summary>
    public double CoachBodySize { get; init; } = 16;

    /// <summary>The shipped defaults.</summary>
    public static PhoneCoachOptions Default { get; } = new();
}
