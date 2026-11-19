using Bazaar.Contracts.Kodo;

namespace Bazaar.Client.Kodo;

/// <summary>One Kodo state colour: the theme token name and its light and dark values (#RRGGBB).</summary>
public sealed record KodoStateToken(KodoDisplayState State, string Name, string Light, string Dark);

/// <summary>
/// The colour tokens for every Kodo state (doc 46 section 14.6). Names follow the client's brush convention,
/// <c>Bazaar.brush.kodo.state.&lt;state&gt;</c>. The values are the defaults a theme may override; each is at least 4.5:1
/// against the default light (#FFFFFF) or dark (#121212) surface. Colour never carries state alone: the pane always shows
/// the state word as well.
/// </summary>
public static class KodoStateTokens
{
    /// <summary>The token name prefix.</summary>
    public const string Prefix = "Bazaar.brush.kodo.state.";

    /// <summary>One token per <see cref="KodoDisplayState"/>, in enum order.</summary>
    public static readonly IReadOnlyList<KodoStateToken> All =
    [
        Make(KodoDisplayState.Healthy, "healthy", "#1B7F3B", "#4CC775"),
        Make(KodoDisplayState.Degraded, "degraded", "#8A5A00", "#F0B429"),
        Make(KodoDisplayState.Failing, "failing", "#B3261E", "#FF8A80"),
        Make(KodoDisplayState.Recovering, "recovering", "#0B5CAD", "#6CB6FF"),
        Make(KodoDisplayState.Frozen, "frozen", "#5B4B9A", "#B8A9F0"),
        Make(KodoDisplayState.Unobserved, "unobserved", "#595959", "#A6A6A6"),
    ];

    /// <summary>The token for <paramref name="state"/>. Throws for a value outside the enum.</summary>
    public static KodoStateToken For(KodoDisplayState state)
    {
        foreach (var token in All)
        {
            if (token.State == state)
            {
                return token;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(state), state, "No Kodo state token.");
    }

    /// <summary>The token for a server health state.</summary>
    public static KodoStateToken For(KodoHealthState state) => For(ToDisplay(state));

    private static KodoDisplayState ToDisplay(KodoHealthState state) => state switch
    {
        KodoHealthState.Healthy => KodoDisplayState.Healthy,
        KodoHealthState.Degraded => KodoDisplayState.Degraded,
        KodoHealthState.Failing => KodoDisplayState.Failing,
        KodoHealthState.Recovering => KodoDisplayState.Recovering,
        KodoHealthState.Frozen => KodoDisplayState.Frozen,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown Kodo health state."),
    };

    private static KodoStateToken Make(KodoDisplayState state, string word, string light, string dark) =>
        new(state, Prefix + word, light, dark);
}
