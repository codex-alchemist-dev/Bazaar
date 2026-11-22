using Microsoft.Extensions.Options;

namespace Bazaar.Server.Gateway.FreeLlmApi;

/// <summary>
/// Settings for the managed FreeLLMAPI sidecar (<c>Bazaar:Gateway:FreeLlmApi:Sidecar</c>). When <see cref="Managed"/> is true the
/// host starts FreeLLMAPI as a child process bound to 127.0.0.1, provisions its admin login and encryption key, and reads the
/// unified key into the state directory. Off by default, so an operator-run FreeLLMAPI keeps working unchanged.
/// </summary>
public sealed class FreeLlmApiSidecarOptions
{
    /// <summary>The FreeLLMAPI release this host is built and tested against. Upgrades are an owner action.</summary>
    public const string PinnedVersion = "0.16.13";

    /// <summary>Loopback only. The sidecar never binds another address.</summary>
    public const string BindHost = "127.0.0.1";

    /// <summary>Default loopback port, matching FreeLLMAPI's own default.</summary>
    public const int DefaultPort = 3001;

    public const string SectionName = FreeLlmApiOptions.SectionName + ":Sidecar";

    public bool Managed { get; set; }

    /// <summary>Node.js executable used to run FreeLLMAPI. FreeLLMAPI needs Node 20 or newer, below 25.</summary>
    public string NodeExecutable { get; set; } = "node";

    /// <summary>Directory holding a built FreeLLMAPI tree (<c>server/dist/index.js</c>). Empty uses <c>&lt;StateDirectory&gt;/app</c>.</summary>
    public string InstallDirectory { get; set; } = "";

    /// <summary>Loopback port FreeLLMAPI listens on.</summary>
    public int Port { get; set; } = DefaultPort;

    /// <summary>Private state: database, generated secrets, optional provider keys file. Empty uses the OS default.</summary>
    public string StateDirectory { get; set; } = "";

    /// <summary>Optional JSON array of <c>{ "platform", "key", "label" }</c> entries for upstream free-tier providers.</summary>
    public string ProviderKeysFile { get; set; } = "";

    /// <summary>How long start waits for FreeLLMAPI to answer, in seconds.</summary>
    public int StartupTimeoutSeconds { get; set; } = 120;

    /// <summary>Resolved state directory: the configured one, or the OS default under the Bazaar data root.</summary>
    public string ResolvedStateDirectory => string.IsNullOrWhiteSpace(StateDirectory)
        ? Path.Combine(OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Bazaar")
            : "/var/lib/bazaar", "freellmapi")
        : StateDirectory;

    public string ResolvedInstallDirectory => string.IsNullOrWhiteSpace(InstallDirectory)
        ? Path.Combine(ResolvedStateDirectory, "app")
        : InstallDirectory;

    public string ResolvedProviderKeysFile => string.IsNullOrWhiteSpace(ProviderKeysFile)
        ? Path.Combine(ResolvedStateDirectory, "provider-keys.json")
        : ProviderKeysFile;

    /// <summary>Secret file that holds the unified key. The gateway reads it through the <c>file:</c> reference.</summary>
    public string UnifiedKeyFile => Path.Combine(ResolvedStateDirectory, "unified-key");

    /// <summary>The <c>file:</c> reference the gateway uses for the unified key when the sidecar is managed.</summary>
    public string UnifiedKeyReference => "file:" + UnifiedKeyFile;
}

/// <summary>Checks the sidecar settings when <see cref="FreeLlmApiSidecarOptions.Managed"/> is true.</summary>
public sealed class FreeLlmApiSidecarOptionsValidator : IValidateOptions<FreeLlmApiSidecarOptions>
{
    public ValidateOptionsResult Validate(string? name, FreeLlmApiSidecarOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Managed)
        {
            return ValidateOptionsResult.Success;
        }

        var section = FreeLlmApiOptions.SectionName + ":Sidecar";
        var failures = new List<string>();
        if (string.IsNullOrWhiteSpace(options.NodeExecutable))
        {
            failures.Add($"{section}:NodeExecutable is required.");
        }

        if (options.Port is < 1024 or > 65535)
        {
            failures.Add($"{section}:Port must be between 1024 and 65535.");
        }

        if (options.StartupTimeoutSeconds is < 5 or > 900)
        {
            failures.Add($"{section}:StartupTimeoutSeconds must be between 5 and 900.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
