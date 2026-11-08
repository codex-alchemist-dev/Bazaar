using Bazaar.Components.ManagedPostgres.Bundle;

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

    /// <summary>The install's pg folder, containing one folder per major. Required in Managed mode.</summary>
    public string BundleRoot { get; set; } = string.Empty;

    /// <summary>The Bazaar data root. The cluster lives under it. Required in Managed mode.</summary>
    public string DataRoot { get; set; } = string.Empty;

    /// <summary>PostgreSQL major to run. Must be a supported major (see <see cref="BundleLayout.SupportedMajors"/>).</summary>
    public int Major { get; set; } = BundleLayout.DefaultMajor;

    public int Port { get; set; } = 54320;

    public string ListenAddress { get; set; } = "127.0.0.1";

    public string SuperuserName { get; set; } = "bazaar_admin";
}
