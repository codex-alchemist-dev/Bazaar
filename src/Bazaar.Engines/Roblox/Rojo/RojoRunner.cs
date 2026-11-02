using System.ComponentModel;

namespace Bazaar.Engines.Roblox.Rojo;

/// <summary>
/// Runs the configured rojo binary for a version probe and for <c>rojo build</c>. Arguments are passed as a list,
/// never through a shell. Every non-success outcome is a <see cref="RojoException"/> with a kind; caller
/// cancellation surfaces as <see cref="OperationCanceledException"/>.
/// </summary>
public sealed class RojoRunner
{
    private readonly RojoOptions _options;
    private readonly IProcessRunner _runner;

    public RojoRunner(RojoOptions options, IProcessRunner? runner = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.BinaryPath))
        {
            throw new ArgumentException("Rojo binary path must not be empty.", nameof(options));
        }

        if (options.Timeout <= TimeSpan.Zero)
        {
            throw new ArgumentException("Rojo timeout must be positive.", nameof(options));
        }

        _options = options;
        _runner = runner ?? new SystemProcessRunner();
    }

    /// <summary>Runs <c>rojo --version</c> and returns its trimmed stdout. Enforces <see cref="RojoOptions.RequiredVersion"/> when set.</summary>
    public async Task<string> GetVersionAsync(CancellationToken cancellationToken = default)
    {
        ProcessResult result = await RunAsync(["--version"], Environment.CurrentDirectory, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw NonZeroExit("--version", result);
        }

        string version = result.StandardOutput.Trim();
        if (version.Length == 0)
        {
            throw new RojoException(RojoErrorKind.EmptyVersionOutput, "rojo --version printed nothing.", result.ExitCode, result.StandardError);
        }

        if (_options.RequiredVersion is { } required && !MatchesVersion(version, required))
        {
            throw new RojoException(
                RojoErrorKind.VersionMismatch,
                $"rojo reports '{version}' but '{required}' is required.",
                result.ExitCode,
                result.StandardError);
        }

        return version;
    }

    /// <summary>
    /// Runs <c>rojo build &lt;project&gt; --output &lt;output&gt;</c>. Both paths must lie under <paramref name="root"/>
    /// (see <see cref="RojoPathGuard"/>). Returns the resolved output path.
    /// </summary>
    public async Task<string> BuildAsync(string root, string projectPath, string outputPath, CancellationToken cancellationToken = default)
    {
        string project = RojoPathGuard.ResolveProjectFile(root, projectPath);
        string output = RojoPathGuard.ResolveOutputFile(root, outputPath);
        string fullRoot = RojoPathGuard.RequireRoot(root);

        ProcessResult result = await RunAsync(BuildArguments(project, output), fullRoot, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw NonZeroExit("build", result);
        }

        return output;
    }

    /// <summary>Argument list for <c>rojo build</c>. Internal so tests can check it without running a process.</summary>
    internal static IReadOnlyList<string> BuildArguments(string projectPath, string outputPath) =>
        ["build", projectPath, "--output", outputPath];

    private async Task<ProcessResult> RunAsync(IReadOnlyList<string> arguments, string workingDirectory, CancellationToken cancellationToken)
    {
        ProcessResult result;
        try
        {
            result = await _runner.RunAsync(_options.BinaryPath, arguments, workingDirectory, _options.Timeout, cancellationToken);
        }
        catch (Win32Exception ex)
        {
            throw new RojoException(RojoErrorKind.BinaryNotFound, $"Could not start '{_options.BinaryPath}': {ex.Message}");
        }
        catch (FileNotFoundException ex)
        {
            throw new RojoException(RojoErrorKind.BinaryNotFound, $"Could not start '{_options.BinaryPath}': {ex.Message}");
        }

        if (result.TimedOut)
        {
            throw new RojoException(
                RojoErrorKind.Timeout,
                $"rojo {arguments[0]} timed out after {_options.Timeout}.",
                standardError: result.StandardError);
        }

        return result;
    }

    private static RojoException NonZeroExit(string command, ProcessResult result)
    {
        string firstLine = result.StandardError.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? "no stderr";
        return new RojoException(
            RojoErrorKind.NonZeroExit,
            $"rojo {command} exited with code {result.ExitCode}: {firstLine}",
            result.ExitCode,
            result.StandardError);
    }

    /// <summary>True when one whitespace-separated token equals the required version, ignoring a leading 'v'.</summary>
    private static bool MatchesVersion(string reported, string required)
    {
        string wanted = required.Trim().TrimStart('v');
        return reported.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Any(token => token.TrimStart('v').Equals(wanted, StringComparison.Ordinal));
    }
}
