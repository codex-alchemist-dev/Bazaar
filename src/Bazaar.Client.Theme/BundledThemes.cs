namespace Bazaar.Client.Theme;

/// <summary>
/// The three themes that ship with the client (docs/design/93-client-visual-system-and-motion.md, Themes): Void (pure black
/// ground, the default), Graphite (blue-grey layers) and Paper (warm light grey). They are ordinary theme files, embedded in
/// this assembly, and go through the same validator as an imported file. Names match the client.theme setting values.
/// </summary>
public static class BundledThemes
{
    /// <summary>The setting value of the shipped default theme.</summary>
    public const string Default = "void";

    /// <summary>The setting values of the bundled themes, default first.</summary>
    public static readonly IReadOnlyList<string> Names = ["void", "graphite", "paper"];

    /// <summary>The raw JSON of a bundled theme file. Throws ArgumentException for a name that is not bundled.</summary>
    public static string ReadJson(string name)
    {
        if (!Names.Contains(name, StringComparer.Ordinal))
        {
            throw new ArgumentException($"'{name}' is not a bundled theme.", nameof(name));
        }

        using Stream stream = typeof(BundledThemes).Assembly.GetManifestResourceStream($"Bazaar.Client.Theme.Themes.{name}.theme.json")
            ?? throw new InvalidOperationException($"Bundled theme '{name}' is missing from the assembly.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Loads and validates a bundled theme. A bundled theme that fails validation is a build defect, so this throws.</summary>
    public static ThemeDefinition Load(string name)
    {
        ThemeLoadResult result = ThemeValidator.Validate(ReadJson(name));
        return result.Definition
            ?? throw new InvalidOperationException(
                $"Bundled theme '{name}' is invalid: " + string.Join("; ", result.Issues.Select(i => $"{i.Path} {i.Code}")));
    }
}
