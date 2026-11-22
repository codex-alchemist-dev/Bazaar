namespace Bazaar.Client.DefaultApp.Linux;

/// <summary>
/// Linux default-app registration with a per-user desktop entry, <c>xdg-mime default</c> and <c>update-desktop-database</c>
/// (doc 30 section 1). It writes only under the user's data directory, runs tools with an argument list and no shell, and
/// trusts only what <c>xdg-mime query default</c> reports back.
/// </summary>
public sealed class XdgDefaultAppRegistrar : IDefaultAppRegistrar
{
    private readonly DefaultAppSettings _settings;
    private readonly IProcessRunner _runner;
    private readonly Func<string, string?> _env;

    /// <summary>Creates the registrar.</summary>
    /// <param name="settings">Validated here; unsafe values throw.</param>
    /// <param name="runner">Runs the tools.</param>
    /// <param name="environment">Reads an environment variable; null reads the process environment.</param>
    /// <exception cref="ArgumentException">The settings are unsafe or malformed.</exception>
    public XdgDefaultAppRegistrar(DefaultAppSettings settings, IProcessRunner runner, Func<string, string?>? environment = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(runner);
        settings.Validate();
        _settings = settings;
        _runner = runner;
        _env = environment ?? Environment.GetEnvironmentVariable;
    }

    /// <summary>The directory that holds the per-user desktop entries.</summary>
    public string ApplicationsDirectory => Path.Combine(DataHome(), "applications");

    /// <summary>Full path of the desktop entry this module manages.</summary>
    public string DesktopFilePath => Path.Combine(ApplicationsDirectory, _settings.DesktopFileId);

    /// <inheritdoc />
    public async Task<DefaultAppStatus> RegisterAsync(CancellationToken ct)
    {
        string path = DesktopFilePath;
        try
        {
            string text = DesktopEntry.Render(_settings);
            if (!File.Exists(path) || await File.ReadAllTextAsync(path, ct).ConfigureAwait(false) != text)
            {
                Directory.CreateDirectory(ApplicationsDirectory);
                string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                await File.WriteAllTextAsync(temp, text, ct).ConfigureAwait(false);
                File.Move(temp, path, overwrite: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new DefaultAppStatus(DefaultAppState.Unavailable, $"Could not write the desktop entry: {e.Message}", null);
        }

        try
        {
            foreach (string mime in _settings.MimeTypes)
            {
                var set = await RunAsync(_settings.XdgMimePath, ["default", _settings.DesktopFileId, mime], ct).ConfigureAwait(false);
                if (set.ExitCode != 0)
                {
                    return new DefaultAppStatus(
                        DefaultAppState.Unavailable,
                        $"xdg-mime could not set the default for {mime} (exit {set.ExitCode}).",
                        Guidance());
                }
            }
        }
        catch (Exception e) when (e is IOException or TimeoutException)
        {
            return new DefaultAppStatus(DefaultAppState.Unavailable, $"xdg-mime is not available: {e.Message}", Guidance());
        }

        await RefreshDatabaseAsync(ct).ConfigureAwait(false);
        return await QueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<DefaultAppStatus> QueryAsync(CancellationToken ct)
    {
        var notDefault = new List<string>();
        try
        {
            foreach (string mime in _settings.MimeTypes)
            {
                var result = await RunAsync(_settings.XdgMimePath, ["query", "default", mime], ct).ConfigureAwait(false);
                if (result.ExitCode != 0)
                {
                    return new DefaultAppStatus(
                        DefaultAppState.Unavailable,
                        $"xdg-mime could not report the default for {mime} (exit {result.ExitCode}).",
                        Guidance());
                }

                if (!string.Equals(result.StandardOutput.Trim(), _settings.DesktopFileId, StringComparison.Ordinal))
                {
                    notDefault.Add(mime);
                }
            }
        }
        catch (Exception e) when (e is IOException or TimeoutException)
        {
            return new DefaultAppStatus(DefaultAppState.Unavailable, $"xdg-mime is not available: {e.Message}", Guidance());
        }

        return notDefault.Count == 0
            ? new DefaultAppStatus(DefaultAppState.Registered, $"{_settings.DisplayName} is the default for {string.Join(", ", _settings.MimeTypes)}.", null)
            : new DefaultAppStatus(
                DefaultAppState.NotDefault,
                $"{_settings.DisplayName} is not the default for {string.Join(", ", notDefault)}.",
                Guidance());
    }

    /// <inheritdoc />
    public async Task<DefaultAppStatus> UnregisterAsync(CancellationToken ct)
    {
        string path = DesktopFilePath;
        try
        {
            if (!File.Exists(path))
            {
                return new DefaultAppStatus(DefaultAppState.NotDefault, "No desktop entry to remove.", null);
            }

            string text = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            if (!text.Contains(DesktopEntry.ManagedMarker, StringComparison.Ordinal))
            {
                return new DefaultAppStatus(DefaultAppState.NotDefault, "The desktop entry was not written by Bazaar and was left alone.", null);
            }

            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new DefaultAppStatus(DefaultAppState.Unavailable, $"Could not remove the desktop entry: {e.Message}", null);
        }

        await RefreshDatabaseAsync(ct).ConfigureAwait(false);
        return new DefaultAppStatus(
            DefaultAppState.NotDefault,
            "Removed the desktop entry. xdg-mime has no unset command, so a stale line in mimeapps.list may remain; desktops ignore it once the entry is gone.",
            null);
    }

    /// <summary>Refreshing the cache is a courtesy: a missing tool or a failure does not change what xdg-mime reports.</summary>
    private async Task RefreshDatabaseAsync(CancellationToken ct)
    {
        try
        {
            await RunAsync(_settings.UpdateDatabasePath, [ApplicationsDirectory], ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or TimeoutException)
        {
            // Ignored on purpose, see above.
        }
    }

    private string DataHome()
    {
        if (_settings.DataHome is not null)
        {
            return _settings.DataHome;
        }

        string? xdg = _env("XDG_DATA_HOME");
        if (!string.IsNullOrEmpty(xdg) && xdg[0] == '/')
        {
            return xdg;
        }

        string home = _env("HOME") is { Length: > 0 } h ? h : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".local", "share");
    }

    private Task<ProcessResult> RunAsync(string tool, string[] args, CancellationToken ct) =>
        _runner.RunAsync(
            new ProcessRequest(tool, args, new Dictionary<string, string> { ["XDG_DATA_HOME"] = DataHome() }, _settings.ToolTimeout),
            ct);

    private string Guidance()
    {
        string desktop = (_env("XDG_CURRENT_DESKTOP") ?? string.Empty).ToUpperInvariant();
        string where = desktop switch
        {
            _ when desktop.Contains("GNOME", StringComparison.Ordinal) => "Open Settings, then Apps, then Default Apps",
            _ when desktop.Contains("KDE", StringComparison.Ordinal) => "Open System Settings, then Applications, then Default Applications",
            _ => "Open your desktop's settings app and look for default applications",
        };
        return $"{where}, and choose {_settings.DisplayName} for {string.Join(", ", _settings.MimeTypes)}. "
            + "If your desktop has no page for link handlers, run: xdg-mime default "
            + $"{_settings.DesktopFileId} {_settings.MimeTypes[0]}";
    }
}
