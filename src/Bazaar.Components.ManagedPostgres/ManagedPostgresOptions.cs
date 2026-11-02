namespace Bazaar.Components.ManagedPostgres;

/// <summary>How Bazaar.Server reaches PostgreSQL (packaging note, section 2).</summary>
public enum DatabaseMode
{
    /// <summary>The host runs a private PostgreSQL from the bundled binaries as a child process.</summary>
    Managed,

    /// <summary>The host connects to the server named by DatabaseConnectionReference.</summary>
    External,
}

/// <summary>Options for the managed PostgreSQL component, bound from the <c>Bazaar:Database</c> section.</summary>
public sealed class ManagedPostgresOptions
{
    public const string SectionName = "Bazaar:Database";

    /// <summary>Safe default: External keeps today's behaviour until the installer chooses Managed.</summary>
    public DatabaseMode Mode { get; set; } = DatabaseMode.External;
}
