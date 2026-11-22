namespace Bazaar.Client.Updater.Linux;

/// <summary>
/// What the updater needs to know about the client and its company server to decide whether a release can be installed
/// (doc 02 versioning, doc 04 startup guard, doc 30 section 5). The caller supplies the values, read when the check runs
/// so they follow a server upgrade; the updater invents none. A value that is not <c>major.minor</c> (protocols) or empty
/// (schema) makes the check fail closed.
/// </summary>
public interface IClientCompatibility
{
    /// <summary>Highest protocol the running build speaks, <c>major.minor</c> (doc 02). An update may not lower it.</summary>
    string RunningProtocol { get; }

    /// <summary>Current protocol of the company server, <c>major.minor</c>, as its <c>welcome</c> or <c>/status</c> reports it.</summary>
    string ServerProtocol { get; }

    /// <summary>
    /// Setting <c>wire.previous_major_supported</c> (doc 02): true when the server also accepts the previous major.
    /// Read from the server's status; when unknown, pass false, the strict reading.
    /// </summary>
    bool ServerAcceptsPreviousMajor { get; }

    /// <summary>Database schema version the company server is at (doc 04). The server refuses to run on any other, so a build must need exactly this.</summary>
    string ServerSchemaVersion { get; }
}
