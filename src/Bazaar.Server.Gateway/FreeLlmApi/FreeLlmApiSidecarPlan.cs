namespace Bazaar.Server.Gateway.FreeLlmApi;

/// <summary>
/// How the host starts FreeLLMAPI: the executable, the entry script, and the environment. It is built from options and the
/// generated encryption key, and contains no process state, so it can be tested without starting Node.
/// </summary>
public sealed record FreeLlmApiSidecarPlan(
    string Executable,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment,
    string BaseUrl,
    string DatabasePath,
    string EntryPointPath)
{
    /// <summary>Entry point inside a built FreeLLMAPI tree.</summary>
    public const string EntryPointRelativePath = "server/dist/index.js";

    public static FreeLlmApiSidecarPlan Create(FreeLlmApiSidecarOptions options, string encryptionKeyHex, string declarativeConfigPath)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(encryptionKeyHex);
        var install = options.ResolvedInstallDirectory;
        var entry = Path.Combine(install, EntryPointRelativePath.Replace('/', Path.DirectorySeparatorChar));
        var database = Path.Combine(options.ResolvedStateDirectory, "freellmapi.db");
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["NODE_ENV"] = "production",
            ["HOST"] = FreeLlmApiSidecarOptions.BindHost,
            ["PORT"] = options.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["FREEAPI_DB_PATH"] = database,
            ["ENCRYPTION_KEY"] = encryptionKeyHex,
            ["FREEAPI_CONFIG_PATH"] = declarativeConfigPath,
        };
        return new FreeLlmApiSidecarPlan(
            Executable: options.NodeExecutable,
            Arguments: [entry],
            WorkingDirectory: install,
            Environment: environment,
            BaseUrl: $"http://{FreeLlmApiSidecarOptions.BindHost}:{options.Port}/v1",
            DatabasePath: database,
            EntryPointPath: entry);
    }
}
