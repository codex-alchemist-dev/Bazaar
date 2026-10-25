namespace Bazaar.Server.Hosting;

/// <summary>Bootstrap values kept in host configuration (doc 03). Everything else lives in the settings registry or behind a secret reference.</summary>
public sealed class BazaarHostOptions
{
    /// <summary>Configuration section, bound from <c>BAZAAR__HOST__*</c> environment variables.</summary>
    public const string SectionName = "Bazaar:Host";

    /// <summary>Absolute path to the data root (<c>%ProgramData%\Bazaar</c> on Windows).</summary>
    public string DataRoot { get; set; } = string.Empty;

    /// <summary>Secret reference for the database connection string, such as <c>env:BAZAAR_DB</c>.</summary>
    public string DatabaseConnectionReference { get; set; } = string.Empty;

    /// <summary>Optional secret reference for the key-encryption key. Null until Windows KEK custody is decided (doc 03, conflict 3).</summary>
    public string? KekReference { get; set; }
}
