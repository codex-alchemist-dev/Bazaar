namespace Bazaar.Client.DefaultApp.Linux;

/// <summary>Whether Bazaar is the default handler, as the desktop reports it.</summary>
public enum DefaultAppState
{
    /// <summary>Every configured type reports Bazaar's desktop entry as the default handler.</summary>
    Registered,

    /// <summary>The entry may exist, but at least one type has another handler or none. The user has a step to take.</summary>
    NotDefault,

    /// <summary>The state cannot be read: <c>xdg-mime</c> is missing, failed or timed out.</summary>
    Unavailable,
}

/// <summary>The honest outcome of a registration, query or removal.</summary>
/// <param name="State">What the desktop reports now.</param>
/// <param name="Message">One sentence on what was found or done.</param>
/// <param name="Guidance">What the user can do next, or null when nothing is needed.</param>
public sealed record DefaultAppStatus(DefaultAppState State, string Message, string? Guidance);
