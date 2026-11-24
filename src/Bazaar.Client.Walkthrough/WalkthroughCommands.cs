using Bazaar.Client.Commands;

namespace Bazaar.Client.Walkthrough;

/// <summary>
/// The walkthrough's palette command (doc 45 section 2.3: re-run through a palette command with its own mnemonic, working name
/// <c>tour</c>). The registry lists it; <see cref="TryRunAsync"/> runs it when the palette picks its id. Replaying never resets settings.
/// </summary>
public static class WalkthroughCommands
{
    public const string ReplayId = "walkthrough.replay";

    /// <summary>The replay command, for the head to hand to the command registry as a local command.</summary>
    public static Command Replay { get; } = new(
        ReplayId,
        "Replay the first-run walkthrough",
        "tour",
        ["walkthrough", "welcome", "intro", "first run", "help"],
        CommandSource.Local);

    /// <summary>Runs the command when <paramref name="commandId"/> is the replay command. Returns false for any other id.</summary>
    public static async Task<bool> TryRunAsync(string commandId, WalkthroughViewModel walkthrough, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(walkthrough);
        if (!string.Equals(commandId, ReplayId, StringComparison.Ordinal)) return false;
        await walkthrough.ReplayAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }
}
