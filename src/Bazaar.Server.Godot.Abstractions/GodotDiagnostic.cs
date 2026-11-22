namespace Bazaar.Server.Godot.Abstractions;

/// <summary>How serious a diagnostic is.</summary>
public enum GodotDiagnosticSeverity
{
    Error,
    Warning,
}

/// <summary>Where in Godot the diagnostic came from.</summary>
public enum GodotDiagnosticSource
{
    /// <summary>An <c>ERROR:</c> or <c>WARNING:</c> line from the engine.</summary>
    Engine,

    /// <summary>A <c>SCRIPT ERROR:</c> line, from GDScript parsing or running.</summary>
    Script,
}

/// <summary>One finding parsed from Godot output. Output is untrusted text; these fields are its parsed, bounded form.</summary>
/// <param name="Severity">Error or warning.</param>
/// <param name="Source">Engine or script.</param>
/// <param name="File">The file as printed, with a leading <c>res://</c> removed. Empty when the output named none.</param>
/// <param name="Line">1-based line, or 0 when unknown.</param>
/// <param name="Message">The message text, bounded in length.</param>
public sealed record GodotDiagnostic(
    GodotDiagnosticSeverity Severity,
    GodotDiagnosticSource Source,
    string File,
    int Line,
    string Message);
