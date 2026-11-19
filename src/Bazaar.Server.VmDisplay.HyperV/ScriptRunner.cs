using System.Text;
using System.Text.Json;
using Bazaar.Server.VmDisplay.Abstractions;

namespace Bazaar.Server.VmDisplay.HyperV;

/// <summary>Runs one of the constant scripts in <see cref="HyperVScripts"/> with its data kept apart from the script text.</summary>
public interface IHyperVScriptRunner
{
    /// <summary>Runs <paramref name="script"/> with <paramref name="parameters"/> available to it as <c>$p.&lt;name&gt;</c>.</summary>
    /// <returns>The script's standard output.</returns>
    /// <exception cref="DisplayException">The run failed, timed out or produced too much output.</exception>
    Task<string> RunAsync(string script, IReadOnlyDictionary<string, string> parameters, CancellationToken ct);
}

/// <summary>
/// Runs PowerShell as <c>powershell.exe -NoProfile -NonInteractive -Command -</c>. The constant script text goes to standard input.
/// The parameters travel as one JSON object in the <see cref="ParametersVariable"/> environment variable, which the scripts read
/// as <c>$p</c>. So no value is ever part of the script text, and none is ever on the command line.
/// </summary>
public sealed class ProcessHyperVScriptRunner : IHyperVScriptRunner
{
    /// <summary>The environment variable that carries the parameter JSON.</summary>
    public const string ParametersVariable = "BAZAAR_DISPLAY_PARAMS";

    private const int MaxStdoutChars = 32 * 1024 * 1024;
    private readonly HyperVOptions _options;
    private readonly IProcessLauncher _launcher;

    /// <summary>Creates the runner with the real process launcher.</summary>
    public ProcessHyperVScriptRunner(HyperVOptions options)
        : this(options, new SystemProcessLauncher())
    {
    }

    internal ProcessHyperVScriptRunner(HyperVOptions options, IProcessLauncher launcher)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(launcher);
        _options = options;
        _launcher = launcher;
    }

    /// <inheritdoc />
    public async Task<string> RunAsync(string script, IReadOnlyDictionary<string, string> parameters, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(script);
        ArgumentNullException.ThrowIfNull(parameters);
        var spec = new ProcessSpec(
            _options.PowerShellPath,
            ["-NoProfile", "-NonInteractive", "-Command", "-"],
            new Dictionary<string, string> { [ParametersVariable] = ParametersJson(parameters) },
            script + "\n",
            TimeSpan.FromSeconds(_options.CommandTimeoutSeconds),
            MaxStdoutChars);
        ProcessResult r = await _launcher.RunAsync(spec, ct).ConfigureAwait(false);
        if (r.ExitCode != 0)
            throw new DisplayException(DisplayFailure.SourceFailed, $"PowerShell exited with code {r.ExitCode}: {Trim(r.StdErr)}");
        return r.StdOut;
    }

    /// <summary>Writes <paramref name="parameters"/> as one JSON object of strings. Non-ASCII text is escaped.</summary>
    internal static string ParametersJson(IReadOnlyDictionary<string, string> parameters)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            foreach ((string key, string value) in parameters)
                w.WriteString(key, value);
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
    }

    private static string Trim(string text)
    {
        string t = text.Trim();
        return t.Length <= 200 ? t : t[..200];
    }
}
