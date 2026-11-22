using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using Bazaar.Server.Engines.Abstractions;
using Bazaar.Server.Godot.Abstractions;

namespace Bazaar.Server.Godot;

/// <summary>
/// Runs Godot headlessly through the injected <see cref="IProcessRunner"/>: build check, import, script check, test runner and
/// export (docs/design/20-engine-integration-api.md, Godot adapter). Godot ignores unknown flags silently, so a run succeeds
/// only on exit code 0 with no <c>ERROR</c> or <c>SCRIPT ERROR</c> lines, and a canary script with a known error must fail
/// before any command runs. Runs are serialised per project directory (the editor locks resources); the build check and the
/// canary touch no project and may run in parallel. A missing binary reports <see cref="GodotRunStatus.Unavailable"/>.
/// </summary>
public sealed class GodotAdapter : IGodotAdapter, IDisposable
{
    private static readonly TimeSpan _versionTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan _canaryTimeout = TimeSpan.FromMinutes(2);

    private static readonly StringComparer _rootComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly GodotAdapterOptions _options;
    private readonly IProcessRunner _runner;
    private readonly (int Major, int Minor, int Patch) _pin;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _projectLocks = new(_rootComparer);
    private readonly SemaphoreSlim _canaryGate = new(1, 1);
    private bool _canaryPassed;

    public GodotAdapter(GodotAdapterOptions options, IProcessRunner runner)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(runner);
        if (string.IsNullOrWhiteSpace(options.BinaryPath))
        {
            throw new ArgumentException("The Godot binary path must not be empty.", nameof(options));
        }

        if (options.CommandTimeoutMinutes is < 1 or > GodotAdapterOptions.MaxCommandTimeoutMinutes)
        {
            throw new ArgumentException("engine.command.timeout_minutes must be between 1 and 120.", nameof(options));
        }

        if (options.MaxOutputCharacters <= 0)
        {
            throw new ArgumentException("The output cap must be positive.", nameof(options));
        }

        _pin = GodotVersion.ParsePin(options.VersionPin)
            ?? throw new ArgumentException("engine.godot.version_pin must look like 4.7 or 4.7.2.", nameof(options));
        _options = options;
        _runner = runner;
    }

    /// <summary>Releases the canary gate and the per-project locks. Do not dispose while runs are in flight.</summary>
    public void Dispose()
    {
        _canaryGate.Dispose();
        foreach (SemaphoreSlim projectLock in _projectLocks.Values)
        {
            projectLock.Dispose();
        }
    }

    /// <inheritdoc />
    public async Task<GodotBuildCheckResult> BuildCheckAsync(string projectRoot, string? exportPreset = null, CancellationToken cancellationToken = default)
    {
        string root = GodotPathGuard.RequireDirectory(projectRoot);
        var failures = new List<string>();
        var warnings = new List<string>();

        ProcessResult process;
        try
        {
            process = await _runner.RunAsync(_options.BinaryPath, ["--version"], root, _versionTimeout, cancellationToken);
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException)
        {
            return Unavailable($"Could not start '{_options.BinaryPath}': {ex.Message}");
        }

        if (process.TimedOut)
        {
            return Failed(null, false, null, null, "godot --version timed out.");
        }

        if (process.ExitCode != 0)
        {
            return Failed(null, false, null, null, $"godot --version exited with code {process.ExitCode}.");
        }

        string stdout = GodotOutputCap.Apply(process.StandardOutput, _options.MaxOutputCharacters, out _);
        GodotVersion? version = GodotVersion.Parse(stdout);
        if (version is null)
        {
            return Failed(null, false, null, null, "godot --version printed an unrecognised version string.");
        }

        GodotBuildVariant binaryVariant = version.IsMono ? GodotBuildVariant.Dotnet : GodotBuildVariant.GdScript;
        bool isEditor = !version.IsTemplate;
        if (!isEditor)
        {
            failures.Add("The binary is an export template build, not an editor build.");
        }

        if (binaryVariant != _options.BuildVariant)
        {
            failures.Add($"The binary is a {binaryVariant} build but engine.godot.build_variant is {_options.BuildVariant}.");
        }

        GodotBuildVariant? projectVariant = null;
        if (File.Exists(Path.Combine(root, "project.godot")))
        {
            projectVariant = GodotProjectInspector.DetectVariant(root);
            if (projectVariant != _options.BuildVariant)
            {
                failures.Add($"The project is a {projectVariant} project but engine.godot.build_variant is {_options.BuildVariant}.");
            }
        }
        else
        {
            failures.Add("The project root has no project.godot.");
        }

        CheckPin(version, failures, warnings);

        if (exportPreset is not null)
        {
            CheckExportPreset(root, exportPreset, version, failures);
        }

        GodotBuildCheckStatus status = failures.Count > 0 ? GodotBuildCheckStatus.Failed
            : warnings.Count > 0 ? GodotBuildCheckStatus.Warning
            : GodotBuildCheckStatus.Passed;
        return new GodotBuildCheckResult(status, version.Display, isEditor, binaryVariant, projectVariant, failures, warnings);
    }

    /// <inheritdoc />
    public async Task<GodotAvailability> EnsureAvailableAsync(CancellationToken cancellationToken = default)
    {
        await _canaryGate.WaitAsync(cancellationToken);
        try
        {
            if (_canaryPassed)
            {
                return new GodotAvailability(true, null);
            }

            GodotAvailability result = await RunCanaryAsync(cancellationToken);
            _canaryPassed = result.Available;
            return result;
        }
        finally
        {
            _canaryGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<GodotRunResult> RunAsync(GodotCommandRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        string root = GodotPathGuard.RequireProjectRoot(request.ProjectRoot);
        IReadOnlyList<string> arguments = GodotCommandBuilder.Build(root, request);
        TimeSpan timeout = ResolveTimeout(request.Timeout);

        GodotAvailability availability = await EnsureAvailableAsync(cancellationToken);
        if (!availability.Available)
        {
            return new GodotRunResult(GodotRunStatus.Unavailable, null, [], availability.Reason, false);
        }

        SemaphoreSlim projectLock = _projectLocks.GetOrAdd(root, _ => new SemaphoreSlim(1, 1));
        await projectLock.WaitAsync(cancellationToken);
        try
        {
            return await ExecuteAsync(arguments, root, timeout, cancellationToken);
        }
        finally
        {
            projectLock.Release();
        }
    }

    private async Task<GodotRunResult> ExecuteAsync(IReadOnlyList<string> arguments, string root, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ProcessResult process;
        try
        {
            process = await _runner.RunAsync(_options.BinaryPath, arguments, root, timeout, cancellationToken);
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException)
        {
            return new GodotRunResult(GodotRunStatus.Unavailable, null, [], $"Could not start '{_options.BinaryPath}': {ex.Message}", false);
        }

        return Interpret(process, timeout);
    }

    private GodotRunResult Interpret(ProcessResult process, TimeSpan timeout)
    {
        if (process.TimedOut)
        {
            return new GodotRunResult(GodotRunStatus.TimedOut, null, [], $"godot timed out after {timeout}.", false);
        }

        string stdout = GodotOutputCap.Apply(process.StandardOutput, _options.MaxOutputCharacters, out bool stdoutCut);
        string stderr = GodotOutputCap.Apply(process.StandardError, _options.MaxOutputCharacters, out bool stderrCut);
        bool truncated = stdoutCut || stderrCut;

        // Godot prints engine and script errors to either stream depending on the platform and flags.
        IReadOnlyList<GodotDiagnostic> diagnostics = GodotOutputParser.Parse(stdout + "\n" + stderr);
        bool hasError = diagnostics.Any(d => d.Severity == GodotDiagnosticSeverity.Error);

        if (process.ExitCode == 0 && !hasError)
        {
            return new GodotRunResult(GodotRunStatus.Succeeded, 0, diagnostics, null, truncated);
        }

        string reason = process.ExitCode != 0
            ? $"godot exited with code {process.ExitCode}."
            : "godot exited with code 0 but printed ERROR lines.";
        return new GodotRunResult(GodotRunStatus.Failed, process.ExitCode, diagnostics, reason, truncated);
    }

    /// <summary>
    /// Runs the embedded canary in a throwaway project. It must fail: exit non-zero or print an error. A pass means Godot
    /// ignored the flags (they are ignored silently), so no later success can be trusted.
    /// </summary>
    private async Task<GodotAvailability> RunCanaryAsync(CancellationToken cancellationToken)
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("bazaar-godot-canary-");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "project.godot"), "config_version=5\n\n[application]\nconfig/name=\"bazaar-canary\"\n", cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "canary.gd"), ReadCanary(), cancellationToken);

            ProcessResult process;
            try
            {
                process = await _runner.RunAsync(
                    _options.BinaryPath,
                    ["--headless", "--check-only", "--script", "canary.gd"],
                    directory.FullName,
                    _canaryTimeout,
                    cancellationToken);
            }
            catch (Exception ex) when (ex is Win32Exception or FileNotFoundException)
            {
                return new GodotAvailability(false, $"Could not start '{_options.BinaryPath}': {ex.Message}");
            }

            if (process.TimedOut)
            {
                return new GodotAvailability(false, "The canary timed out.");
            }

            GodotRunResult outcome = Interpret(process, _canaryTimeout);
            return outcome.Status == GodotRunStatus.Failed
                ? new GodotAvailability(true, null)
                : new GodotAvailability(false, "The canary script with a known error did not fail: Godot is ignoring the check flags.");
        }
        finally
        {
            try
            {
                directory.Delete(recursive: true);
            }
            catch (IOException)
            {
                // Best effort: the OS may still hold the folder briefly.
            }
        }
    }

    internal static string ReadCanary()
    {
        using Stream stream = typeof(GodotAdapter).Assembly.GetManifestResourceStream("canary.gd")
            ?? throw new InvalidOperationException("The embedded canary.gd resource is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private TimeSpan ResolveTimeout(TimeSpan? requested)
    {
        TimeSpan max = TimeSpan.FromMinutes(GodotAdapterOptions.MaxCommandTimeoutMinutes);
        if (requested is null)
        {
            return TimeSpan.FromMinutes(_options.CommandTimeoutMinutes);
        }

        if (requested <= TimeSpan.Zero)
        {
            throw new GodotRequestRejectedException("Godot command timeout must be positive.");
        }

        return requested.Value > max ? max : requested.Value;
    }

    private void CheckPin(GodotVersion version, List<string> failures, List<string> warnings)
    {
        if (version.Major != _pin.Major || version.Minor != _pin.Minor)
        {
            failures.Add($"Godot {version.Display} is not on the pinned {_pin.Major}.{_pin.Minor} line (pin {_options.VersionPin}).");
        }
        else if (version.Patch != _pin.Patch)
        {
            string message = $"Godot {version.Display} differs from the pinned version {_options.VersionPin}.";
            (_options.FollowMaintenance ? warnings : failures).Add(message);
        }
    }

    private void CheckExportPreset(string root, string preset, GodotVersion version, List<string> failures)
    {
        try
        {
            GodotCommandBuilder.ValidatePreset(root, preset);
        }
        catch (GodotRequestRejectedException ex)
        {
            failures.Add(ex.Message);
            return;
        }

        string? templatesRoot = _options.ExportTemplatesRoot ?? DefaultTemplatesRoot();
        string folder = templatesRoot is null ? string.Empty : Path.Combine(templatesRoot, version.TemplatesFolder);
        if (templatesRoot is null || !Directory.Exists(folder) || !Directory.EnumerateFileSystemEntries(folder).Any())
        {
            failures.Add($"Export templates for {version.TemplatesFolder} are not installed.");
        }
    }

    private static string? DefaultTemplatesRoot()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return appData.Length == 0 ? null : Path.Combine(appData, "Godot", "export_templates");
        }

        if (OperatingSystem.IsMacOS())
        {
            return home.Length == 0 ? null : Path.Combine(home, "Library", "Application Support", "Godot", "export_templates");
        }

        string? dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrEmpty(dataHome))
        {
            return Path.Combine(dataHome, "godot", "export_templates");
        }

        return home.Length == 0 ? null : Path.Combine(home, ".local", "share", "godot", "export_templates");
    }

    private static GodotBuildCheckResult Unavailable(string reason) =>
        new(GodotBuildCheckStatus.Unavailable, null, false, null, null, [reason], []);

    private static GodotBuildCheckResult Failed(string? version, bool isEditor, GodotBuildVariant? binary, GodotBuildVariant? project, string reason) =>
        new(GodotBuildCheckStatus.Failed, version, isEditor, binary, project, [reason], []);
}
