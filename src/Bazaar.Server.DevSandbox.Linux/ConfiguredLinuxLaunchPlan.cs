namespace Bazaar.Server.DevSandbox.Linux;

/// <summary>Operator settings for <see cref="ConfiguredLinuxLaunchPlan"/> (doc 48 section 2.1, host wiring: the launch plan is operator-configured until the module packaging format is decided).</summary>
public sealed class ConfiguredLinuxLaunchOptions
{
    /// <summary>Absolute directory holding one sub-directory per module and version: <c>&lt;ModuleRoot&gt;/&lt;moduleId&gt;/&lt;moduleVersion&gt;</c>. Required.</summary>
    public string ModuleRoot { get; init; } = string.Empty;

    /// <summary>Absolute path of the executable that runs a module (for example a .NET host or a module runner). Required.</summary>
    public string Executable { get; init; } = string.Empty;

    /// <summary>
    /// Argument templates. <c>{module}</c> becomes the module's directory and <c>{work}</c> the per-run work directory; nothing else is
    /// substituted and the result is passed as an argument vector, never through a shell.
    /// </summary>
    public IReadOnlyList<string> Arguments { get; init; } = ["{module}"];

    /// <summary>Extra absolute paths the process may read when hardening is on (the module directory is always added).</summary>
    public IReadOnlyList<string> AdditionalReadPaths { get; init; } = [];
}

/// <summary>
/// A launch plan driven only by operator configuration. It refuses module ids and versions that could leave the module root, and modules that
/// are not installed there, so a spec can never point the run at an arbitrary path.
/// </summary>
public sealed class ConfiguredLinuxLaunchPlan : ILinuxSandboxLaunchPlan
{
    private readonly ConfiguredLinuxLaunchOptions _options;

    /// <summary>Creates the plan; throws when the options are not absolute paths.</summary>
    public ConfiguredLinuxLaunchPlan(ConfiguredLinuxLaunchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Path.IsPathRooted(options.ModuleRoot) || !Path.IsPathRooted(options.Executable))
        {
            throw new ArgumentException("ModuleRoot and Executable must be absolute paths.", nameof(options));
        }

        _options = options;
    }

    /// <inheritdoc />
    public LinuxLaunchCommand Create(SandboxRunSpec spec, string workDirectory)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentException.ThrowIfNullOrEmpty(workDirectory);
        var module = ResolveModuleDirectory(spec);
        var args = _options.Arguments
            .Select(a => a.Replace("{module}", module, StringComparison.Ordinal).Replace("{work}", workDirectory, StringComparison.Ordinal))
            .ToList();
        var reads = new List<string>(_options.AdditionalReadPaths) { module };
        return new LinuxLaunchCommand(_options.Executable, args, reads);
    }

    private string ResolveModuleDirectory(SandboxRunSpec spec)
    {
        if (!IsSafeSegment(spec.ModuleId) || !IsSafeSegment(spec.ModuleVersion))
        {
            throw new InvalidOperationException("The module id or version is not a plain name.");
        }

        var dir = Path.Combine(_options.ModuleRoot, spec.ModuleId, spec.ModuleVersion);
        if (!Directory.Exists(dir))
        {
            throw new InvalidOperationException("The module is not installed under the configured module root.");
        }

        return dir;
    }

    private static bool IsSafeSegment(string s) =>
        s.Length > 0 && s != "." && s != ".." && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '+');
}
