using System.Diagnostics;

namespace Bazaar.Engines.Processes;

/// <summary>
/// Builds the environment of an engine child process from an allowlist. The server's own environment holds secrets
/// (provider keys, database passwords); a child must never inherit it wholesale, so everything not listed here is dropped.
/// </summary>
internal static class ChildProcessEnvironment
{
    /// <summary>
    /// Variables a child needs to start and find its runtime. None of these carries a credential by convention. Matching is
    /// case-insensitive, as environment names are on Windows.
    /// </summary>
    private static readonly HashSet<string> _allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        "PATH", "PATHEXT", "HOME", "USERPROFILE", "SYSTEMROOT", "SYSTEMDRIVE", "WINDIR",
        "TEMP", "TMP", "TMPDIR", "LANG", "LC_ALL", "LC_CTYPE", "TZ",
        "APPDATA", "LOCALAPPDATA", "PROGRAMDATA", "PROGRAMFILES", "PROCESSOR_ARCHITECTURE", "NUMBER_OF_PROCESSORS", "OS",
        "DOTNET_ROOT", "DOTNET_CLI_HOME", "DOTNET_NOLOGO", "DOTNET_CLI_TELEMETRY_OPTOUT",
    };

    /// <summary>True when the named variable may be passed to a child.</summary>
    public static bool IsAllowed(string name) => _allowed.Contains(name);

    /// <summary>
    /// Replaces the start info's inherited environment with the allowlisted subset of this process's environment, then
    /// adds <paramref name="extra"/> (values the server chose on purpose, for example a per-adapter setting).
    /// </summary>
    public static void Apply(ProcessStartInfo startInfo, IReadOnlyDictionary<string, string>? extra = null)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        startInfo.Environment.Clear();
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string name && entry.Value is string value && IsAllowed(name))
            {
                startInfo.Environment[name] = value;
            }
        }

        if (extra is null)
        {
            return;
        }

        foreach ((string name, string value) in extra)
        {
            startInfo.Environment[name] = value;
        }
    }
}
