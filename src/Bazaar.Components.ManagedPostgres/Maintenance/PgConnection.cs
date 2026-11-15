using System.Globalization;

namespace Bazaar.Components.ManagedPostgres.Maintenance;

/// <summary>
/// Where and how a maintenance tool connects. The password reaches the tool only as PGPASSWORD in its environment, never on
/// the command line. The host is always loopback, because the managed cluster listens on 127.0.0.1 only.
/// </summary>
public sealed record PgConnection(int Major, int Port, string User, string Password, string Database)
{
    /// <summary>Environment for one tool run.</summary>
    public IReadOnlyDictionary<string, string> Environment => new Dictionary<string, string>
    {
        ["PGPASSWORD"] = Password,
    };

    /// <summary>Port as the tools expect it on the command line.</summary>
    public string PortText => Port.ToString(CultureInfo.InvariantCulture);
}

/// <summary>A maintenance step failed. The message names the step and carries the tool's output, never its arguments or secrets.</summary>
public sealed class PgMaintenanceException(string message) : Exception(message);
