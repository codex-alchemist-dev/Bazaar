namespace Bazaar.Server.Godot.Abstractions;

/// <summary>The outcome of the build check.</summary>
public enum GodotBuildCheckStatus
{
    /// <summary>Every check passed.</summary>
    Passed,

    /// <summary>Usable, with warnings (for example a different maintenance release).</summary>
    Warning,

    /// <summary>A check failed; commands should not run.</summary>
    Failed,

    /// <summary>The binary could not be started.</summary>
    Unavailable,
}

/// <summary>What the build check found (docs/design/20-engine-integration-api.md, Godot adapter: build check first).</summary>
/// <param name="Status">The overall outcome.</param>
/// <param name="Version">The version as parsed from <c>--version</c> (for example <c>4.7.2.stable</c>), or null when it could not be parsed.</param>
/// <param name="IsEditorBuild">True when the binary is an editor build, not an export template.</param>
/// <param name="BinaryVariant">The variant of the binary, or null when unknown.</param>
/// <param name="ProjectVariant">The variant of the project, detected from <c>project.godot</c> and the project files.</param>
/// <param name="Failures">Reasons the check failed.</param>
/// <param name="Warnings">Non-blocking findings.</param>
public sealed record GodotBuildCheckResult(
    GodotBuildCheckStatus Status,
    string? Version,
    bool IsEditorBuild,
    GodotBuildVariant? BinaryVariant,
    GodotBuildVariant? ProjectVariant,
    IReadOnlyList<string> Failures,
    IReadOnlyList<string> Warnings);
