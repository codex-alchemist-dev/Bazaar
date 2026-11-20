namespace Bazaar.Contracts.Wire;

/// <summary>
/// Request <c>prefs.set</c> (doc 02): sets one preference for the caller. The envelope <c>id</c> is the idempotency key.
/// </summary>
/// <param name="Key">A dotted settings-registry key the registry lets a person change.</param>
/// <param name="Value">The canonical string form the registry defines for the key.</param>
public sealed record PrefsSet(string Key, string Value);

/// <summary>Reply to <c>prefs.set</c>.</summary>
/// <param name="Revision">The profile revision after the change, as <c>ui.changed</c> will announce it.</param>
public sealed record PrefsSetResult(long Revision);
