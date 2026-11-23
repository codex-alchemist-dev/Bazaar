namespace Bazaar.Client.DefaultApp.Linux;

/// <summary>
/// Settings for the default-terminal registrar (doc 30 section 1). Validation reuses <see cref="DefaultAppSettings"/> so the
/// desktop file ID, display name and paths follow the same fail-closed rules as the handler entry.
/// </summary>
public sealed record DefaultTerminalSettings
{
    /// <summary>Absolute path of the installed Bazaar terminal binary, used in <c>Exec</c>.</summary>
    public required string ExecPath { get; init; }

    /// <summary>Desktop entry ID of Bazaar's terminal. A separate file from the handler entry, so each can be removed alone.</summary>
    public string DesktopFileId { get; init; } = "bazaar-terminal.desktop";

    /// <summary>Name shown in terminal pickers.</summary>
    public string DisplayName { get; init; } = "Bazaar Terminal";

    /// <summary>Overrides <c>$XDG_DATA_HOME</c>; null reads the environment, then <c>~/.local/share</c>.</summary>
    public string? DataHome { get; init; }

    /// <summary>Overrides <c>$XDG_CONFIG_HOME</c>; null reads the environment, then <c>~/.config</c>.</summary>
    public string? ConfigHome { get; init; }

    /// <summary>Overrides <c>$XDG_CONFIG_DIRS</c> (colon separated); null reads the environment, then <c>/etc/xdg</c>.</summary>
    public string? ConfigDirs { get; init; }

    /// <summary>Path or name of <c>gsettings</c>, read only.</summary>
    public string GSettingsPath { get; init; } = "gsettings";

    /// <summary>Names of the KDE config readers, tried in order, read only.</summary>
    public IReadOnlyList<string> KReadConfigPaths { get; init; } = ["kreadconfig6", "kreadconfig5"];

    /// <summary>How long each tool may run.</summary>
    public TimeSpan ToolTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Checks every value, failing closed.</summary>
    /// <exception cref="ArgumentException">A value is unsafe or malformed.</exception>
    public void Validate()
    {
        new DefaultAppSettings { ExecPath = ExecPath, DesktopFileId = DesktopFileId, DisplayName = DisplayName, DataHome = DataHome, ToolTimeout = ToolTimeout }.Validate();
        if (ConfigHome is not null && (ConfigHome.Length == 0 || ConfigHome[0] != '/' || ConfigHome.Any(char.IsControl)))
        {
            throw new ArgumentException("ConfigHome must be an absolute path.", nameof(ConfigHome));
        }

        if (ConfigDirs is not null && ConfigDirs.Any(char.IsControl))
        {
            throw new ArgumentException("ConfigDirs must not contain control characters.", nameof(ConfigDirs));
        }

        if (string.IsNullOrWhiteSpace(GSettingsPath) || KReadConfigPaths is null || KReadConfigPaths.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Tool paths must be set.");
        }
    }
}
