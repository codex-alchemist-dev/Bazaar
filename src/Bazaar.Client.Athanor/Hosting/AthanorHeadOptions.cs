namespace Bazaar.Client.Athanor.Hosting;

/// <summary>
/// The operator settings a head reads for the editor (doc 57 §2.2). Read from the environment until the settings service
/// reaches clients. Pure: the environment and the platform's data folder are passed in.
/// </summary>
public sealed record AthanorHeadOptions
{
    public const string HostVariable = "BAZAAR_ATHANOR_HOST";
    public const string DataVariable = "BAZAAR_ATHANOR_DATA";
    public const string GtkHelperVariable = "BAZAAR_ATHANOR_GTK_HELPER";

    /// <summary>Which host to try first. <see cref="EditorHostKind.Auto"/> uses the platform order.</summary>
    public EditorHostKind Host { get; init; } = EditorHostKind.Auto;

    /// <summary>The folder for drafts and the WebView2 profile. Absolute.</summary>
    public required string DataDirectory { get; init; }

    /// <summary>The GTK helper executable or assembly, or null when none is configured.</summary>
    public string? GtkHelperPath { get; init; }

    /// <summary>A problem found while reading the settings (an unknown host value, a relative data folder), or null.</summary>
    public string? Warning { get; init; }

    /// <summary>Where unsaved drafts are kept.</summary>
    public string DraftsDirectory => Path.Combine(DataDirectory, "athanor-drafts");

    /// <summary>The WebView2 user-data folder (profile and cache).</summary>
    public string WebView2UserDataFolder => Path.Combine(DataDirectory, "athanor-webview2");

    /// <param name="env">Reads an environment variable.</param>
    /// <param name="defaultDataDirectory">The platform's per-user data folder for Bazaar, used when the operator sets none.</param>
    public static AthanorHeadOptions FromEnvironment(Func<string, string?> env, string defaultDataDirectory)
    {
        ArgumentNullException.ThrowIfNull(env);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultDataDirectory);

        var warnings = new List<string>();
        var host = EditorHostKind.Auto;
        var hostText = Clean(env(HostVariable));
        if (!EditorHostSelector.TryParse(hostText, out host))
        {
            warnings.Add($"{HostVariable} '{hostText}' is not auto, webview2, gtk, avaloniaedit or plain; using auto.");
            host = EditorHostKind.Auto;
        }

        var data = defaultDataDirectory;
        var dataText = Clean(env(DataVariable));
        if (dataText is not null)
        {
            if (Path.IsPathFullyQualified(dataText))
            {
                data = dataText;
            }
            else
            {
                warnings.Add($"{DataVariable} must be an absolute path; using the default folder.");
            }
        }

        return new AthanorHeadOptions
        {
            Host = host,
            DataDirectory = data,
            GtkHelperPath = Clean(env(GtkHelperVariable)),
            Warning = warnings.Count == 0 ? null : string.Join(' ', warnings),
        };
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
