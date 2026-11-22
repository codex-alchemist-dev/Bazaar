namespace Bazaar.Server.Godot.Abstractions;

/// <summary>The headless commands the adapter can run (docs/design/20-engine-integration-api.md, Godot adapter).</summary>
public enum GodotCommandKind
{
    /// <summary><c>--headless --import</c>.</summary>
    Import,

    /// <summary><c>--headless --check-only --script &lt;file&gt;</c>.</summary>
    CheckScript,

    /// <summary><c>--headless --script &lt;runner&gt;</c>: the test entry point.</summary>
    RunScript,

    /// <summary><c>--headless --export-release "&lt;preset&gt;" &lt;dir&gt;</c>.</summary>
    Export,
}

/// <summary>One command to run in a Godot project.</summary>
/// <param name="ProjectRoot">The project directory, which must hold <c>project.godot</c>.</param>
/// <param name="Kind">The command.</param>
/// <param name="Script">The <c>.gd</c> file for <see cref="GodotCommandKind.CheckScript"/> and <see cref="GodotCommandKind.RunScript"/>, relative to the root.</param>
/// <param name="Preset">The export preset name for <see cref="GodotCommandKind.Export"/>; it must exist in <c>export_presets.cfg</c>.</param>
/// <param name="OutputDirectory">The existing output directory for <see cref="GodotCommandKind.Export"/>, relative to the root.</param>
/// <param name="Timeout">Overrides the configured per-command timeout; capped at 120 minutes.</param>
public sealed record GodotCommandRequest(
    string ProjectRoot,
    GodotCommandKind Kind,
    string? Script = null,
    string? Preset = null,
    string? OutputDirectory = null,
    TimeSpan? Timeout = null);
