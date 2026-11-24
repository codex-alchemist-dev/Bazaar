using System.Text;

namespace Bazaar.Client.DefaultApp.Linux;

/// <summary>
/// Default-terminal registration through the xdg-terminal-exec <c>xdg-terminals.list</c> file (doc 30 section 1). It writes
/// only under the user's data and config directories, never changes the GNOME or KDE keys (GNOME ignores its key now, KDE's
/// is unreliable), and reads those, and the Debian alternative, only to tell the user what else is set.
/// </summary>
public sealed class XdgDefaultTerminalRegistrar : IDefaultTerminalRegistrar
{
    private const string _listName = "xdg-terminals.list";
    private const string _gnomeSchema = "org.gnome.desktop.default-applications.terminal";

    private readonly DefaultTerminalSettings _settings;
    private readonly IProcessRunner _runner;
    private readonly Func<string, string?> _env;

    /// <summary>Creates the registrar.</summary>
    /// <param name="settings">Validated here; unsafe values throw.</param>
    /// <param name="runner">Runs the read-only tools.</param>
    /// <param name="environment">Reads an environment variable; null reads the process environment.</param>
    /// <exception cref="ArgumentException">The settings are unsafe or malformed.</exception>
    public XdgDefaultTerminalRegistrar(DefaultTerminalSettings settings, IProcessRunner runner, Func<string, string?>? environment = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(runner);
        settings.Validate();
        _settings = settings;
        _runner = runner;
        _env = environment ?? Environment.GetEnvironmentVariable;
    }

    /// <summary>Full path of the terminal desktop entry this module manages.</summary>
    public string DesktopFilePath => Path.Combine(DataHome(), "applications", _settings.DesktopFileId);

    /// <summary>Full path of the user's <c>xdg-terminals.list</c>, the one file this module edits.</summary>
    public string UserListPath => Path.Combine(ConfigHome(), _listName);

    /// <inheritdoc />
    public async Task<DefaultAppStatus> RegisterAsync(CancellationToken ct)
    {
        try
        {
            string entry = DesktopEntry.RenderTerminal(_settings);
            string path = DesktopFilePath;
            if (!File.Exists(path) || await File.ReadAllTextAsync(path, ct).ConfigureAwait(false) != entry)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await WriteAtomicAsync(path, entry, ct).ConfigureAwait(false);
            }

            string existing = File.Exists(UserListPath) ? await File.ReadAllTextAsync(UserListPath, ct).ConfigureAwait(false) : string.Empty;
            string updated = WithBazaarFirst(existing);
            if (updated != existing)
            {
                Directory.CreateDirectory(ConfigHome());
                await WriteAtomicAsync(UserListPath, updated, ct).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new DefaultAppStatus(DefaultAppState.Unavailable, $"Could not write the terminal configuration: {e.Message}", null);
        }

        return await QueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<DefaultAppStatus> QueryAsync(CancellationToken ct)
    {
        string? firstId = null;
        string? firstFile = null;
        try
        {
            foreach (string file in CandidateListFiles())
            {
                if (!File.Exists(file))
                {
                    continue;
                }

                string? id = FirstExplicitId(await File.ReadAllTextAsync(file, ct).ConfigureAwait(false));
                if (id is not null)
                {
                    firstId = id;
                    firstFile = file;
                    break;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new DefaultAppStatus(DefaultAppState.Unavailable, $"Could not read the terminal configuration: {e.Message}", Guidance());
        }

        string others = await OtherMechanismsAsync(ct).ConfigureAwait(false);
        if (string.Equals(firstId, _settings.DesktopFileId, StringComparison.Ordinal))
        {
            return new DefaultAppStatus(
                DefaultAppState.Registered,
                $"{_settings.DisplayName} is first in {firstFile}, so xdg-terminal-exec launchers use it.{others}",
                null);
        }

        string found = firstId is null
            ? "No xdg-terminals.list names a terminal, so xdg-terminal-exec picks the first terminal entry it finds in an undefined order."
            : $"{firstFile} names {firstId} first.";
        return new DefaultAppStatus(DefaultAppState.NotDefault, $"{_settings.DisplayName} is not the default terminal. {found}{others}", Guidance());
    }

    /// <inheritdoc />
    public async Task<DefaultAppStatus> UnregisterAsync(CancellationToken ct)
    {
        try
        {
            if (File.Exists(UserListPath))
            {
                string existing = await File.ReadAllTextAsync(UserListPath, ct).ConfigureAwait(false);
                string updated = WithoutBazaar(existing);
                if (updated != existing)
                {
                    if (string.IsNullOrWhiteSpace(updated))
                    {
                        File.Delete(UserListPath);
                    }
                    else
                    {
                        await WriteAtomicAsync(UserListPath, updated, ct).ConfigureAwait(false);
                    }
                }
            }

            string path = DesktopFilePath;
            if (File.Exists(path))
            {
                string text = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
                if (!text.Contains(DesktopEntry.ManagedMarker, StringComparison.Ordinal))
                {
                    return new DefaultAppStatus(DefaultAppState.NotDefault, "The terminal desktop entry was not written by Bazaar and was left alone.", null);
                }

                File.Delete(path);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new DefaultAppStatus(DefaultAppState.Unavailable, $"Could not remove the terminal configuration: {e.Message}", null);
        }

        return new DefaultAppStatus(DefaultAppState.NotDefault, "Removed Bazaar's terminal from your xdg-terminals.list and its desktop entry.", null);
    }

    /// <summary>Reads one line per mechanism that is set but not controlled here, so the user is told what else may win.</summary>
    private async Task<string> OtherMechanismsAsync(CancellationToken ct)
    {
        var sb = new StringBuilder();
        string? gnome = await TryReadAsync(_settings.GSettingsPath, ["get", _gnomeSchema, "exec"], ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(gnome))
        {
            sb.Append(" GNOME's legacy exec key reads ").Append(gnome.Trim('\'', '"')).Append(" (current GNOME ignores it).");
        }

        foreach (string tool in _settings.KReadConfigPaths)
        {
            string? kde = await TryReadAsync(tool, ["--file", "kdeglobals", "--group", "General", "--key", "TerminalService"], ct).ConfigureAwait(false);
            if (kde is not null)
            {
                if (kde.Length > 0)
                {
                    sb.Append(" KDE's TerminalService reads ").Append(kde).Append('.');
                }

                break;
            }
        }

        return sb.ToString();
    }

    /// <summary>Output of a read-only tool, trimmed, or null when it is missing, fails or times out.</summary>
    private async Task<string?> TryReadAsync(string tool, string[] args, CancellationToken ct)
    {
        try
        {
            var result = await _runner.RunAsync(
                new ProcessRequest(tool, args, new Dictionary<string, string>(), _settings.ToolTimeout),
                ct).ConfigureAwait(false);
            return result.ExitCode == 0 ? result.StandardOutput.Trim() : null;
        }
        catch (Exception e) when (e is IOException or TimeoutException)
        {
            return null;
        }
    }

    /// <summary>Config files in xdg-terminal-exec priority order: per directory, each desktop's file, then the plain file.</summary>
    private IEnumerable<string> CandidateListFiles()
    {
        var dirs = new List<string> { ConfigHome() };
        string raw = _settings.ConfigDirs ?? _env("XDG_CONFIG_DIRS") ?? string.Empty;
        dirs.AddRange(raw.Split(':', StringSplitOptions.RemoveEmptyEntries).Where(d => d[0] == '/'));
        if (string.IsNullOrEmpty(raw))
        {
            dirs.Add("/etc/xdg");
        }

        var desktops = (_env("XDG_CURRENT_DESKTOP") ?? string.Empty)
            .Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Select(d => d.ToLowerInvariant())
            .Where(d => d.IndexOfAny(['/', '\\']) < 0);
        foreach (string dir in dirs)
        {
            foreach (string desktop in desktops)
            {
                yield return Path.Combine(dir, desktop + "-" + _listName);
            }

            yield return Path.Combine(dir, _listName);
        }
    }

    /// <summary>The first explicit entry ID (without any action), skipping comments, directives and exclusions.</summary>
    internal static string? FirstExplicitId(string text)
    {
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or '-' or '+' or '/')
            {
                continue;
            }

            int colon = line.IndexOf(':', StringComparison.Ordinal);
            return colon < 0 ? line : line[..colon];
        }

        return null;
    }

    private string WithBazaarFirst(string existing)
    {
        var lines = RemoveBazaarLines(existing);
        int at = lines.FindIndex(l => l.Trim() is { Length: > 0 } t && t[0] != '#');
        lines.Insert(at < 0 ? lines.Count : at, _settings.DesktopFileId);
        return string.Join('\n', lines) + "\n";
    }

    private string WithoutBazaar(string existing)
    {
        var lines = RemoveBazaarLines(existing);
        return lines.Count == 0 ? string.Empty : string.Join('\n', lines) + "\n";
    }

    /// <summary>Drops this module's ID (plain or excluded with a leading minus), keeps every other line and comment.</summary>
    private List<string> RemoveBazaarLines(string text)
    {
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        while (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        lines.RemoveAll(l =>
        {
            string t = l.Trim().TrimStart('-');
            int colon = t.IndexOf(':', StringComparison.Ordinal);
            return string.Equals(colon < 0 ? t : t[..colon], _settings.DesktopFileId, StringComparison.Ordinal);
        });
        return lines;
    }

    private static async Task WriteAtomicAsync(string path, string text, CancellationToken ct)
    {
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllTextAsync(temp, text, ct).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }

    private string DataHome() => _settings.DataHome ?? AbsoluteEnv("XDG_DATA_HOME") ?? Path.Combine(Home(), ".local", "share");

    private string ConfigHome() => _settings.ConfigHome ?? AbsoluteEnv("XDG_CONFIG_HOME") ?? Path.Combine(Home(), ".config");

    private string? AbsoluteEnv(string name) => _env(name) is { Length: > 0 } v && v[0] == '/' ? v : null;

    private string Home() => _env("HOME") is { Length: > 0 } h ? h : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private string Guidance()
    {
        string desktop = (_env("XDG_CURRENT_DESKTOP") ?? string.Empty).ToUpperInvariant();
        string settingsPage = desktop switch
        {
            _ when desktop.Contains("GNOME", StringComparison.Ordinal) => "GNOME has no settings page for the terminal; its old exec key is ignored.",
            _ when desktop.Contains("KDE", StringComparison.Ordinal) => "In KDE, open System Settings, then Applications, then Default Applications, and choose the terminal there; KDE keeps its own setting.",
            _ => "Your desktop may keep its own terminal setting.",
        };
        return $"Run the register command to put {_settings.DisplayName} first in {UserListPath} (used by xdg-terminal-exec launchers). "
            + settingsPage
            + " Launchers that use Debian's x-terminal-emulator need root, which Bazaar will not take: run "
            + $"sudo update-alternatives --install /usr/bin/x-terminal-emulator x-terminal-emulator '{_settings.ExecPath}' 50 "
            + $"and then sudo update-alternatives --set x-terminal-emulator '{_settings.ExecPath}'";
    }
}
