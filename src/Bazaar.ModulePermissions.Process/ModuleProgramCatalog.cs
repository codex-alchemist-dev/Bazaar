using System.Collections.Frozen;

namespace Bazaar.ModulePermissions.Process;

/// <summary>One program the host offers to modules.</summary>
/// <param name="ExecutablePath">Full host path of the executable. The module never sees it.</param>
/// <param name="WorkingDirectory">Full host path of the directory the program starts in. The host chooses one that holds nothing the module may not read.</param>
/// <param name="Environment">Extra environment variables the host sets on purpose for this program. Never a secret: a module that runs the program can make it print them.</param>
public sealed record ModuleProgram(string ExecutablePath, string WorkingDirectory, IReadOnlyDictionary<string, string>? Environment = null);

/// <summary>
/// The programs a host offers to modules, by name. A module asks for a name (the scope of a <c>process</c> permission, for
/// example <c>tools/ffmpeg</c>) and never for a host path, so a grant can only ever start something the host put here.
/// Names are compared exactly, as permission targets are. The catalog does not change after it is built.
/// </summary>
public sealed class ModuleProgramCatalog
{
    private readonly FrozenDictionary<string, ModuleProgram> _programs;

    /// <summary>Builds the catalog. Throws when a name is not a valid permission target or a path is not a full path.</summary>
    public ModuleProgramCatalog(IReadOnlyDictionary<string, ModuleProgram> programs)
    {
        ArgumentNullException.ThrowIfNull(programs);
        foreach (var (name, program) in programs)
        {
            if (!ModuleTargetRules.IsValidTarget(name) || name[0] == '/' || name[^1] == '/')
            {
                throw new ArgumentException("A program name is not a valid permission target.", nameof(programs));
            }

            ArgumentNullException.ThrowIfNull(program);
            if (!Path.IsPathFullyQualified(program.ExecutablePath) || !Path.IsPathFullyQualified(program.WorkingDirectory))
            {
                throw new ArgumentException("A program's executable and working directory must be full paths.", nameof(programs));
            }

            if (program.Environment is { } environment)
            {
                foreach (var (variable, value) in environment)
                {
                    if (string.IsNullOrEmpty(variable) || variable.Contains('=', StringComparison.Ordinal) || variable.Contains('\0', StringComparison.Ordinal) || value is null || value.Contains('\0', StringComparison.Ordinal))
                    {
                        throw new ArgumentException("A program's environment holds a name or value that cannot be set.", nameof(programs));
                    }
                }
            }
        }

        _programs = programs.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <summary>The program offered under this exact name, or null.</summary>
    public ModuleProgram? Find(string name) => _programs.GetValueOrDefault(name);
}
