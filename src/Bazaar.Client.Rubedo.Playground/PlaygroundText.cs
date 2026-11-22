using Bazaar.Localisation;

namespace Bazaar.Client.Rubedo.Playground;

/// <summary>
/// The playground's message keys and its English catalogue (doc 41c section 7: labels and text use localisation keys).
/// A head passes its own <see cref="LocalisedText"/> chain when it has translations; the default is the English catalogue.
/// </summary>
public static class PlaygroundText
{
    public const string Title = "playground.title";
    public const string SourceLabel = "playground.source.label";
    public const string ModeLabel = "playground.mode.label";
    public const string ModeExpression = "playground.mode.expression";
    public const string ModeView = "playground.mode.view";
    public const string ContextHeading = "playground.context.heading";
    public const string FormatLabel = "playground.format.label";
    public const string ResultLabel = "playground.result.label";
    public const string DiagnosticsLabel = "playground.diagnostics.label";
    public const string ResultValid = "playground.result.valid";
    public const string DiagnosticLine = "playground.diagnostic.line";
    public const string FormatFailed = "playground.format.failed";
    public const string FormatUnavailable = "playground.format.unavailable";

    /// <summary>The label key of the context control for an identifier of <c>RubedoSchema.Standard</c>.</summary>
    public static string ContextLabel(string identifier) => "playground.context." + identifier;

    /// <summary>The English catalogue as a one-entry fallback chain.</summary>
    public static LocalisedText Default { get; } = new([English()]);

    private static MessageCatalog English() => MessageCatalog.Create("en", new Dictionary<string, string>
    {
        [Title] = "Rubedo playground",
        [SourceLabel] = "Source",
        [ModeLabel] = "Document mode",
        [ModeExpression] = "Expression",
        [ModeView] = "View document",
        [ContextHeading] = "Context",
        [FormatLabel] = "Format source",
        [ResultLabel] = "Result",
        [DiagnosticsLabel] = "Diagnostics",
        [ResultValid] = "Valid view document",
        [DiagnosticLine] = "{code} at {start}-{end}: {message}",
        [FormatFailed] = "Format failed: fix the diagnostics first",
        [FormatUnavailable] = "Formatting applies to expressions only",
        [ContextLabel("deviceClass")] = "Device class",
        [ContextLabel("display")] = "Display",
        [ContextLabel("posture")] = "Posture",
        [ContextLabel("widthDp")] = "Width (dp)",
        [ContextLabel("heightDp")] = "Height (dp)",
        [ContextLabel("textScale")] = "Text scale",
        [ContextLabel("reduceMotion")] = "Reduce motion",
        [ContextLabel("isOwner")] = "Is owner",
        [ContextLabel("colorScheme")] = "Color scheme",
    });
}
