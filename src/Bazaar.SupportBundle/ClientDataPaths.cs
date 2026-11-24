namespace Bazaar.SupportBundle;

/// <summary>
/// Where the client keeps its logs. <c>BAZAAR_DATA_DIR</c> overrides the default, which is how the clean-run check
/// simulates a machine that never ran Bazaar without touching the real profile (doc 37 section 4d). The OS "known folder"
/// API does not follow the <c>LOCALAPPDATA</c> variable, so the override is explicit.
/// </summary>
public static class ClientDataPaths
{
    /// <summary>The environment variable that overrides the data directory.</summary>
    public const string DataDirVariable = "BAZAAR_DATA_DIR";

    /// <summary>The data directory: the override when set, otherwise <c>%LOCALAPPDATA%\Bazaar</c> (or the platform equivalent).</summary>
    public static string DataDir(Func<string, string?>? env = null)
    {
        var value = (env ?? Environment.GetEnvironmentVariable)(DataDirVariable);
        return string.IsNullOrWhiteSpace(value)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bazaar")
            : value;
    }

    /// <summary>The client log file inside <paramref name="dataDir"/>.</summary>
    public static string LogFile(string dataDir) => Path.Combine(dataDir, "logs", "client.log");
}
