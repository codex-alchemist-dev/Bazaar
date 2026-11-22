using System.Globalization;

namespace Bazaar.Contracts.Wire;

/// <summary>
/// What a client updater needs from <see cref="Welcome"/> to judge whether a build fits the server (doc 02 Versioning).
/// Pure mapping, so a head can use it without referencing Bazaar.Server. Unknown values are null.
/// </summary>
/// <param name="ServerProtocol">The server's protocol as <c>major.minor</c>.</param>
/// <param name="ChosenProtocol">
/// The protocol this connection runs on as <c>major.minor</c> (the chosen major with the server's minor), or null when the
/// server asked the client to upgrade and no version was chosen.
/// </param>
/// <param name="SchemaVersion">The server's database schema version (doc 04), or null when it does not report one.</param>
/// <param name="PreviousMajorSupported">Whether the server accepts the previous protocol major, or null when it does not say.</param>
/// <param name="UpgradeRequired">True when no version overlapped and the server closed with 4426.</param>
/// <param name="MinimumBuild">With <paramref name="UpgradeRequired"/>: the lowest acceptable client build, or null.</param>
public sealed record WelcomeCompatibility(
    string? ServerProtocol,
    string? ChosenProtocol,
    string? SchemaVersion,
    bool? PreviousMajorSupported,
    bool UpgradeRequired,
    string? MinimumBuild)
{
    /// <summary>Maps a welcome. A welcome with <see cref="Welcome.UpgradeRequired"/> set names the server's own major; nothing was chosen.</summary>
    public static WelcomeCompatibility From(Welcome welcome)
    {
        ArgumentNullException.ThrowIfNull(welcome);
        var upgrade = welcome.UpgradeRequired == true;
        var protocol = Format(welcome.Version, welcome.Minor);
        return new WelcomeCompatibility(
            protocol,
            upgrade ? null : protocol,
            string.IsNullOrWhiteSpace(welcome.SchemaVersion) ? null : welcome.SchemaVersion,
            welcome.PreviousMajorSupported,
            upgrade,
            string.IsNullOrWhiteSpace(welcome.MinimumBuild) ? null : welcome.MinimumBuild);
    }

    /// <summary>Formats a protocol as <c>major.minor</c> with no padding, or null for a negative part.</summary>
    public static string? Format(int major, int minor) =>
        major < 0 || minor < 0
            ? null
            : string.Create(CultureInfo.InvariantCulture, $"{major}.{minor}");
}
