using System.IO;
using System.Text.RegularExpressions;
using Bazaar.Server.Godot.Abstractions;

namespace Bazaar.Server.Godot;

/// <summary>Reads <c>project.godot</c> and <c>export_presets.cfg</c> as text. Nothing is executed; the files are untrusted input.</summary>
internal static partial class GodotProjectInspector
{
    /// <summary>
    /// Detects the project's build variant: .NET when <c>config/features</c> in <c>project.godot</c> lists <c>C#</c>, or when a
    /// <c>.csproj</c> or <c>.sln</c> sits in the project root; otherwise GDScript.
    /// </summary>
    public static GodotBuildVariant DetectVariant(string fullRoot)
    {
        string projectFile = Path.Combine(fullRoot, "project.godot");
        if (File.Exists(projectFile))
        {
            foreach (string line in File.ReadLines(projectFile))
            {
                Match match = FeaturesLine().Match(line);
                if (match.Success && FeatureCSharp().IsMatch(match.Groups["list"].Value))
                {
                    return GodotBuildVariant.Dotnet;
                }
            }
        }

        bool hasSolution = Directory.EnumerateFiles(fullRoot, "*.csproj", SearchOption.TopDirectoryOnly).Any()
            || Directory.EnumerateFiles(fullRoot, "*.sln", SearchOption.TopDirectoryOnly).Any()
            || Directory.EnumerateFiles(fullRoot, "*.slnx", SearchOption.TopDirectoryOnly).Any();
        return hasSolution ? GodotBuildVariant.Dotnet : GodotBuildVariant.GdScript;
    }

    /// <summary>
    /// Returns the preset names from <c>export_presets.cfg</c>: the <c>name=</c> key in each <c>[preset.N]</c> section (not the
    /// <c>[preset.N.options]</c> sections). An absent file yields no presets.
    /// </summary>
    public static IReadOnlyList<string> ReadPresetNames(string fullRoot)
    {
        string path = Path.Combine(fullRoot, "export_presets.cfg");
        var names = new List<string>();
        if (!File.Exists(path))
        {
            return names;
        }

        bool inPreset = false;
        foreach (string raw in File.ReadLines(path))
        {
            string line = raw.Trim();
            if (line.StartsWith('['))
            {
                inPreset = PresetSection().IsMatch(line);
                continue;
            }

            if (!inPreset)
            {
                continue;
            }

            Match match = NameLine().Match(line);
            if (match.Success)
            {
                names.Add(match.Groups["name"].Value);
            }
        }

        return names;
    }

    [GeneratedRegex(@"^\s*config/features\s*=\s*PackedStringArray\((?<list>.*)\)\s*$")]
    private static partial Regex FeaturesLine();

    [GeneratedRegex("\"C#\"")]
    private static partial Regex FeatureCSharp();

    [GeneratedRegex(@"^\[preset\.\d+\]$")]
    private static partial Regex PresetSection();

    [GeneratedRegex("^name\\s*=\\s*\"(?<name>[^\"]*)\"$")]
    private static partial Regex NameLine();
}
