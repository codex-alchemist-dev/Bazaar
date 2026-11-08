using System.Net;
using System.Text.RegularExpressions;

namespace Bazaar.Components.ManagedPostgres;

/// <summary>Settings for one managed PostgreSQL child process (packaging note, sections 3 and 6 P3).</summary>
public sealed class PostgresProcessOptions
{
    private static readonly Regex _superuserNamePattern = new("^[a-z_][a-z0-9_]{0,62}$", RegexOptions.CultureInvariant);

    /// <summary>Folder holding initdb, postgres and pg_ctl, for example <c>pg/18/bin</c>.</summary>
    public string BinDirectory { get; set; } = string.Empty;

    /// <summary>Cluster folder (PGDATA). initdb creates it on first run.</summary>
    public string DataDirectory { get; set; } = string.Empty;

    /// <summary>TCP port, pinned per cluster. Default 54320, the first port of the packaging range.</summary>
    public int Port { get; set; } = 54320;

    /// <summary>Address postgres listens on. Readiness is probed on the same address (0.0.0.0 and :: probe loopback).</summary>
    public string ListenAddress { get; set; } = "127.0.0.1";

    /// <summary>Superuser role created by initdb.</summary>
    public string SuperuserName { get; set; } = "bazaar_admin";

    /// <summary>How long start waits for the port to accept connections. Default 60 seconds.</summary>
    public TimeSpan StartupTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>How long stop waits after a fast shutdown, and again after an immediate one, before killing the process. Default 15 seconds.</summary>
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Restarts allowed after unexpected exits before the controller gives up. Default 5. A run that lasts RestartResetAfter resets the count.</summary>
    public int MaxRestartAttempts { get; set; } = 5;

    /// <summary>Delay before the first restart. Each further consecutive attempt doubles it. Default 1 second.</summary>
    public TimeSpan RestartBaseDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Upper bound for the restart delay. Default 30 seconds.</summary>
    public TimeSpan RestartMaxDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>A child that stays up at least this long resets the consecutive restart count. Default 5 minutes.</summary>
    public TimeSpan RestartResetAfter { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Throws when a setting cannot work.</summary>
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(BinDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(DataDirectory);

        if (Port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(Port), Port, "Port must be between 1 and 65535.");
        }

        if (!IPAddress.TryParse(ListenAddress, out _))
        {
            throw new ArgumentException("ListenAddress must be an IP address.", nameof(ListenAddress));
        }

        if (!_superuserNamePattern.IsMatch(SuperuserName))
        {
            throw new ArgumentException("SuperuserName must be a lowercase PostgreSQL identifier.", nameof(SuperuserName));
        }

        RequirePositive(StartupTimeout, nameof(StartupTimeout));
        RequirePositive(ShutdownTimeout, nameof(ShutdownTimeout));
        RequirePositive(RestartBaseDelay, nameof(RestartBaseDelay));
        RequirePositive(RestartMaxDelay, nameof(RestartMaxDelay));
        RequirePositive(RestartResetAfter, nameof(RestartResetAfter));

        if (MaxRestartAttempts < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxRestartAttempts), MaxRestartAttempts, "MaxRestartAttempts cannot be negative.");
        }

        if (RestartMaxDelay < RestartBaseDelay)
        {
            throw new ArgumentException("RestartMaxDelay cannot be shorter than RestartBaseDelay.", nameof(RestartMaxDelay));
        }
    }

    private static void RequirePositive(TimeSpan value, string name)
    {
        if (value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(name, value, "Must be a positive duration.");
        }
    }
}
