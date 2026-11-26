using System.Text.RegularExpressions;

namespace Bazaar.Client.DefaultApp.Linux;

/// <summary>
/// Settings for per-user default-app registration on Linux (doc 30 section 1, "Default-app registration"). The types are
/// configurable [PROPOSED] defaults: doc 45 names the <c>bazaar://</c> scheme, no doc names a file type.
/// </summary>
public sealed partial record DefaultAppSettings
{
    /// <summary>Default for <see cref="MimeTypes"/>: the <c>bazaar://</c> sign-in hand-off (doc 45).</summary>
    public static readonly IReadOnlyList<string> DefaultMimeTypes = ["x-scheme-handler/bazaar"];

    /// <summary>Absolute path of the installed client binary, used in <c>Exec</c>.</summary>
    public required string ExecPath { get; init; }

    /// <summary>Desktop file name, which is also the desktop entry ID that <c>xdg-mime</c> stores.</summary>
    public string DesktopFileId { get; init; } = "bazaar.desktop";

    /// <summary>Name shown in the desktop's app lists.</summary>
    public string DisplayName { get; init; } = "Bazaar";

    /// <summary>MIME types to register, for example <c>x-scheme-handler/bazaar</c>.</summary>
    public IReadOnlyList<string> MimeTypes { get; init; } = DefaultMimeTypes;

    /// <summary>Overrides <c>$XDG_DATA_HOME</c>; null reads the environment, then <c>~/.local/share</c>.</summary>
    public string? DataHome { get; init; }

    /// <summary>Path or name of <c>xdg-mime</c>.</summary>
    public string XdgMimePath { get; init; } = "xdg-mime";

    /// <summary>Path or name of <c>update-desktop-database</c>.</summary>
    public string UpdateDatabasePath { get; init; } = "update-desktop-database";

    /// <summary>How long each tool may run.</summary>
    public TimeSpan ToolTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Checks every value, failing closed on anything the Desktop Entry spec cannot carry safely.</summary>
    /// <exception cref="ArgumentException">A value is unsafe or malformed.</exception>
    public void Validate()
    {
        if (string.IsNullOrEmpty(ExecPath) || ExecPath[0] != '/')
        {
            throw new ArgumentException("ExecPath must be an absolute path.", nameof(ExecPath));
        }

        if (ExecPath.Any(c => c is '"' or '\'' || char.IsControl(c)))
        {
            throw new ArgumentException("ExecPath must not contain quotes, newlines or control characters.", nameof(ExecPath));
        }

        if (DesktopFileId is null || !FileIdPattern().IsMatch(DesktopFileId))
        {
            throw new ArgumentException("DesktopFileId must be a plain file name ending in .desktop.", nameof(DesktopFileId));
        }

        if (string.IsNullOrWhiteSpace(DisplayName) || DisplayName.Any(char.IsControl))
        {
            throw new ArgumentException("DisplayName must be text without control characters.", nameof(DisplayName));
        }

        if (MimeTypes is null || MimeTypes.Count == 0 || MimeTypes.Any(m => m is null || !MimePattern().IsMatch(m)))
        {
            throw new ArgumentException("MimeTypes must hold at least one valid type.", nameof(MimeTypes));
        }

        if (DataHome is not null && (DataHome.Length == 0 || DataHome[0] != '/' || DataHome.Any(char.IsControl)))
        {
            throw new ArgumentException("DataHome must be an absolute path.", nameof(DataHome));
        }

        if (string.IsNullOrWhiteSpace(XdgMimePath) || string.IsNullOrWhiteSpace(UpdateDatabasePath) || ToolTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentException("Tool paths and the timeout must be set.");
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]*\.desktop$", RegexOptions.CultureInvariant)]
    private static partial Regex FileIdPattern();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9.+-]*/[A-Za-z0-9][A-Za-z0-9.+-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex MimePattern();
}
