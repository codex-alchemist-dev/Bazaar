namespace Bazaar.Server.Setup;

/// <summary>
/// The doors a setup plan came through (doc 45 section 1.1). Flags combine, so one plan can use a file and flags together.
/// The plan hash never depends on the doors; only the entry path recorded in the audit row does (doc 45 section 1.2).
/// </summary>
[Flags]
public enum SetupDoors
{
    None = 0,

    /// <summary>The declarative file, <c>bazaar-setup.json</c>.</summary>
    File = 1,

    /// <summary>Environment variables with the <c>BAZAAR__SETUP__</c> prefix.</summary>
    EnvironmentVariables = 2,

    /// <summary>Command-line <c>--set path=value</c> flags.</summary>
    Flags = 4,
}

/// <summary>The rules that turn the doors used into the entry path of an apply (doc 45 section 1.2).</summary>
public static class SetupDoorRules
{
    /// <summary>
    /// The entry path for a plan built from <paramref name="doors"/>. Mixed use takes the highest-precedence door that contributed:
    /// flags, then environment variables, then the file. A plan with no door at all reports <see cref="SetupEntryPath.File"/>; the
    /// loader rejects it anyway, because the required keys are missing.
    /// </summary>
    public static SetupEntryPath EntryPathFor(SetupDoors doors)
    {
        if (doors.HasFlag(SetupDoors.Flags))
        {
            return SetupEntryPath.Cli;
        }

        return doors.HasFlag(SetupDoors.EnvironmentVariables) ? SetupEntryPath.Env : SetupEntryPath.File;
    }
}
