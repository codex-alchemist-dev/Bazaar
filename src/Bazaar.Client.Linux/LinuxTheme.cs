using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Bazaar.Client.Commands;
using Bazaar.Client.Theme;

namespace Bazaar.Client.Linux;

/// <summary>
/// Applies a bundled theme (Void, Graphite, Paper) to the running Avalonia application (doc 93 Themes, doc 29). The tokens land in
/// one <see cref="ResourceDictionary"/> in <c>Application.Resources.MergedDictionaries</c> under the same keys
/// <see cref="AvaloniaThemeGenerator"/> emits (<c>Bazaar.&lt;token&gt;</c>, and <c>Bazaar.brush.&lt;token&gt;</c> for colours), so a runtime
/// switch swaps that one dictionary and <c>DynamicResource</c> consumers update live. Built directly from the validated tokens, with
/// no runtime XAML loader. Unknown names fall back to Void and are logged.
/// </summary>
public static class LinuxTheme
{
    /// <summary>Environment variable that overrides the flag and the preference.</summary>
    public const string EnvironmentVariable = "BAZAAR_THEME";

    /// <summary>The command-line flag, as <c>--theme name</c> or <c>--theme=name</c>.</summary>
    public const string Flag = "--theme";

    private const string _keyPrefix = "Bazaar.";
    private const string _brushPrefix = "Bazaar.brush.";

    private static ResourceDictionary? _applied;

    /// <summary>The name of the theme applied last (a bundled setting value), or null before any apply.</summary>
    public static string? Current { get; private set; }

    /// <summary>
    /// Picks the requested theme name: the environment override first, then the flag, then the stored preference, else the default.
    /// The result may still be unknown; <see cref="Apply"/> falls back.
    /// </summary>
    public static string ResolveName(IReadOnlyList<string> args, Func<string, string?> getEnvironmentVariable, string? preference = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);
        if (getEnvironmentVariable(EnvironmentVariable) is { } fromEnvironment && !string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return fromEnvironment.Trim();
        }

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg.StartsWith(Flag + "=", StringComparison.Ordinal))
            {
                return arg[(Flag.Length + 1)..].Trim();
            }

            if (arg == Flag && i + 1 < args.Count)
            {
                return args[i + 1].Trim();
            }
        }

        return string.IsNullOrWhiteSpace(preference) ? BundledThemes.Default : preference.Trim();
    }

    /// <summary>Resolves the name from flag and environment and applies it to <see cref="Application.Current"/>. Call once at start.</summary>
    public static string ApplyAtStart(IReadOnlyList<string> args, Func<string, string?> getEnvironmentVariable, Action<string> log, string? preference = null) =>
        Apply(Application.Current, ResolveName(args, getEnvironmentVariable, preference), log);

    /// <summary>
    /// Applies the bundled theme called <paramref name="name"/> (case-insensitive) to <paramref name="app"/>. An unknown name logs
    /// and applies Void. Returns the name actually applied.
    /// </summary>
    public static string Apply(Application? app, string name, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(log);
        var normalised = name.Trim().ToLowerInvariant();
        if (!BundledThemes.Names.Contains(normalised, StringComparer.Ordinal))
        {
            log($"Unknown theme '{name}'. Using '{BundledThemes.Default}'. Known themes: {string.Join(", ", BundledThemes.Names)}.");
            normalised = BundledThemes.Default;
        }

        if (app is null)
        {
            return normalised;
        }

        var dictionary = Build(BundledThemes.Load(normalised));
        var merged = app.Resources.MergedDictionaries;
        if (_applied is not null)
        {
            merged.Remove(_applied);
        }

        merged.Add(dictionary);
        _applied = dictionary;
        Current = normalised;
        return normalised;
    }

    /// <summary>Builds the resource dictionary for a validated theme, with the keys <see cref="AvaloniaThemeGenerator"/> emits.</summary>
    public static ResourceDictionary Build(ThemeDefinition theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        var dictionary = new ResourceDictionary();
        foreach (var spec in ThemeTokens.Required)
        {
            var value = theme.Tokens[spec.Name];
            var key = _keyPrefix + spec.Name;
            switch (spec.Kind)
            {
                case TokenKind.Colour:
                    var colour = Color.Parse(ToAvaloniaColour(value));
                    dictionary[key] = colour;
                    dictionary[_brushPrefix + spec.Name] = new SolidColorBrush(colour);
                    break;
                case TokenKind.FontFamily:
                    dictionary[key] = new FontFamily(value);
                    break;
                case TokenKind.Length:
                case TokenKind.Number:
                case TokenKind.Duration:
                    dictionary[key] = double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
                    break;
                case TokenKind.Choice:
                    dictionary[key] = value;
                    break;
                case TokenKind.Flag:
                    dictionary[key] = value == "true";
                    break;
            }
        }

        return dictionary;
    }

    // Theme colours are #RRGGBB or #RRGGBBAA; Avalonia reads #AARRGGBB, so the alpha pair moves to the front.
    private static string ToAvaloniaColour(string value) =>
        value.Length == 9 ? string.Concat("#", value.AsSpan(7, 2), value.AsSpan(1, 6)) : value;
}

/// <summary>The palette commands that switch the bundled theme at runtime (doc 93). Local commands.</summary>
public static class LinuxThemeCommands
{
    /// <summary>Id of the Void command.</summary>
    public const string VoidId = "theme.void";

    /// <summary>Id of the Graphite command.</summary>
    public const string GraphiteId = "theme.graphite";

    /// <summary>Id of the Paper command.</summary>
    public const string PaperId = "theme.paper";

    /// <summary>The palette entries, one per bundled theme.</summary>
    public static IReadOnlyList<Command> All { get; } =
    [
        new(VoidId, "Theme: Void", "theme void", ["theme", "dark", "black"], CommandSource.Local),
        new(GraphiteId, "Theme: Graphite", "theme graphite", ["theme", "dark", "blue"], CommandSource.Local),
        new(PaperId, "Theme: Paper", "theme paper", ["theme", "light"], CommandSource.Local),
    ];

    /// <summary>Runs a theme command and returns the applied theme name, or null when <paramref name="id"/> is not a theme command.</summary>
    public static string? Run(string id, Application? app, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(id);
        return id switch
        {
            VoidId => LinuxTheme.Apply(app, "void", log),
            GraphiteId => LinuxTheme.Apply(app, "graphite", log),
            PaperId => LinuxTheme.Apply(app, "paper", log),
            _ => null,
        };
    }
}
