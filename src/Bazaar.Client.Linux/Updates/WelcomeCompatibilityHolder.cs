using Bazaar.Client.Updater.Linux;
using Bazaar.Contracts.Wire;

namespace Bazaar.Client.Linux.Updates;

/// <summary>
/// Holds the compatibility facts of the last <see cref="Welcome"/> and shows them to the updater as
/// <see cref="IClientCompatibility"/>. Until a welcome arrives every server fact is unknown, which the updater reads as
/// "do not apply" (<see cref="UpdateStatus.CompatibilityUnknown"/>).
/// </summary>
public sealed class WelcomeCompatibilityHolder : IClientCompatibility
{
    private WelcomeCompatibility? _latest;

    /// <summary>Creates a holder for a client that speaks <paramref name="runningProtocol"/> (<c>major.minor</c>).</summary>
    public WelcomeCompatibilityHolder(string runningProtocol)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runningProtocol);
        RunningProtocol = runningProtocol;
    }

    /// <summary>The last welcome's facts, or null before the first one.</summary>
    public WelcomeCompatibility? Latest => Volatile.Read(ref _latest);

    /// <inheritdoc />
    public string RunningProtocol { get; }

    /// <inheritdoc />
    public string ServerProtocol => Latest?.ServerProtocol ?? string.Empty;

    /// <inheritdoc />
    public bool ServerAcceptsPreviousMajor => Latest?.PreviousMajorSupported ?? false;

    /// <inheritdoc />
    public string ServerSchemaVersion => Latest?.SchemaVersion ?? string.Empty;

    /// <summary>Records the facts of a welcome.</summary>
    public void Record(Welcome welcome) => Volatile.Write(ref _latest, WelcomeCompatibility.From(welcome));

    /// <summary>
    /// The reason an update must not be applied on what the last welcome says, or null when the facts are known and the
    /// server is not demanding a newer client. The updater repeats the full protocol and schema check against the manifest.
    /// </summary>
    public string? Refusal()
    {
        var latest = Latest;
        if (latest is null)
        {
            return "The server has not reported its version yet, so the update cannot be checked against it.";
        }

        if (latest.UpgradeRequired)
        {
            return "The server needs a newer client than this build; update from the server's own feed once it is ready.";
        }

        return latest.ServerProtocol is null || latest.SchemaVersion is null
            ? "The server did not report its protocol or schema version, so the update cannot be checked against it."
            : null;
    }
}
