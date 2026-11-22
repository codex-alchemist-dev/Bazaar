using System.Text.RegularExpressions;
using Bazaar.Server.Godot.Abstractions;

namespace Bazaar.Server.Godot;

/// <summary>
/// Builds Godot argument lists (never shell strings). Every path is jailed under the project root and passed relative to it
/// with forward slashes; the process runs with the root as its working directory, where Godot finds <c>project.godot</c>.
/// </summary>
internal static partial class GodotCommandBuilder
{
    /// <summary>Longest export preset name accepted.</summary>
    public const int MaxPresetLength = 64;

    /// <summary>Validates the request and returns the argument list. Throws <see cref="GodotRequestRejectedException"/> on any violation.</summary>
    public static IReadOnlyList<string> Build(string fullRoot, GodotCommandRequest request)
    {
        switch (request.Kind)
        {
            case GodotCommandKind.Import:
                return ["--headless", "--import"];
            case GodotCommandKind.CheckScript:
                return ["--headless", "--check-only", "--script", RequireScript(fullRoot, request.Script)];
            case GodotCommandKind.RunScript:
                return ["--headless", "--script", RequireScript(fullRoot, request.Script)];
            case GodotCommandKind.Export:
                string preset = ValidatePreset(fullRoot, request.Preset);
                if (request.OutputDirectory is null)
                {
                    throw new GodotRequestRejectedException("An export needs an output directory.");
                }

                return ["--headless", "--export-release", preset, GodotPathGuard.ResolveDirectory(fullRoot, request.OutputDirectory)];
            default:
                throw new GodotRequestRejectedException($"Unknown Godot command '{request.Kind}'.");
        }
    }

    /// <summary>Checks a preset name for the safe character set and for presence in <c>export_presets.cfg</c>.</summary>
    public static string ValidatePreset(string fullRoot, string? preset)
    {
        if (string.IsNullOrEmpty(preset) || preset.Length > MaxPresetLength || !SafePreset().IsMatch(preset))
        {
            throw new GodotRequestRejectedException("Export preset name is missing or uses characters outside letters, digits, space, '.', '_', '-', '(' and ')'.");
        }

        if (!GodotProjectInspector.ReadPresetNames(fullRoot).Contains(preset, StringComparer.Ordinal))
        {
            throw new GodotRequestRejectedException($"Export preset '{preset}' is not in export_presets.cfg.");
        }

        return preset;
    }

    private static string RequireScript(string fullRoot, string? script)
    {
        if (script is null)
        {
            throw new GodotRequestRejectedException("A script path is required.");
        }

        string relative = GodotPathGuard.ResolveFile(fullRoot, script);
        if (!relative.EndsWith(".gd", StringComparison.OrdinalIgnoreCase))
        {
            throw new GodotRequestRejectedException($"Script '{relative}' is not a .gd file.");
        }

        return relative;
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9 ._()\-]*\z")]
    private static partial Regex SafePreset();
}
