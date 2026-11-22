namespace Bazaar.Server.Luau.Abstractions;

/// <summary>How serious a diagnostic is.</summary>
public enum LuauSeverity
{
    Error,
    Warning,
    Info,
}

/// <summary>One finding from a Luau tool. Tool output is untrusted text; these fields are its parsed, bounded form.</summary>
/// <param name="Tool">The tool that reported it.</param>
/// <param name="File">The file path as the tool printed it (relative to the project root). Empty when the tool named none.</param>
/// <param name="Line">1-based line, or 0 when the tool reported no line (for example a whole-file formatting finding).</param>
/// <param name="Column">1-based column, or 0 when unknown.</param>
/// <param name="Severity">The severity mapped from the tool's own level.</param>
/// <param name="Code">The tool's rule or error code (for example <c>UnknownGlobal</c> or <c>unused_variable</c>). Empty when none.</param>
/// <param name="Message">The tool's message text.</param>
public sealed record LuauDiagnostic(
    LuauToolKind Tool,
    string File,
    int Line,
    int Column,
    LuauSeverity Severity,
    string Code,
    string Message);
