namespace Bazaar.Server.Godot.Abstractions;

/// <summary>The Godot build variant (setting <c>engine.godot.build_variant</c>): the standard GDScript build or the .NET (mono) build.</summary>
public enum GodotBuildVariant
{
    GdScript,
    Dotnet,
}

/// <summary>
/// Configurable inputs for the Godot adapter (docs/design/20-engine-integration-api.md, Settings). The setting keys are noted
/// on each property; the caller reads the settings and passes the values in.
/// </summary>
public sealed record GodotAdapterOptions
{
    /// <summary>Default output cap per stream, in characters (1 MiB of ASCII). Output beyond it is cut and marked.</summary>
    public const int DefaultMaxOutputCharacters = 1_048_576;

    /// <summary>Largest per-command timeout, in minutes (doc 16's hard maximum).</summary>
    public const int MaxCommandTimeoutMinutes = 120;

    /// <summary>Path to the Godot editor binary, or a bare name resolved through PATH.</summary>
    public string BinaryPath { get; init; } = "godot";

    /// <summary>Setting <c>engine.godot.build_variant</c>.</summary>
    public GodotBuildVariant BuildVariant { get; init; } = GodotBuildVariant.GdScript;

    /// <summary>Setting <c>engine.godot.version_pin</c>: <c>major.minor</c> or <c>major.minor.patch</c>.</summary>
    public string VersionPin { get; init; } = "4.7.2";

    /// <summary>
    /// Setting <c>engine.godot.follow_maintenance</c>. True: a different patch release of the pinned major.minor is a warning.
    /// False: any difference from the pin fails the build check. A different major.minor always fails.
    /// </summary>
    public bool FollowMaintenance { get; init; } = true;

    /// <summary>Setting <c>engine.command.timeout_minutes</c>: 1 to 120.</summary>
    public int CommandTimeoutMinutes { get; init; } = 15;

    /// <summary>
    /// Folder that holds Godot's installed export templates (one sub-folder per version, for example <c>4.7.2.stable</c>).
    /// Null means the per-user default for the current OS.
    /// </summary>
    public string? ExportTemplatesRoot { get; init; }

    /// <summary>Cap on each captured output stream, in characters. Output beyond it is dropped and the result is marked truncated.</summary>
    public int MaxOutputCharacters { get; init; } = DefaultMaxOutputCharacters;
}
