namespace Bazaar.Server.Luau.Abstractions;

/// <summary>Diagnostics from every requested tool, plus one report per tool in request order.</summary>
/// <param name="Diagnostics">Diagnostics from all tools that produced output. Each carries its tool.</param>
/// <param name="Tools">One report per requested tool, whatever its outcome.</param>
public sealed record LuauCheckResult(IReadOnlyList<LuauDiagnostic> Diagnostics, IReadOnlyList<LuauToolReport> Tools);

/// <summary>What happened when one tool ran.</summary>
/// <param name="Tool">The tool.</param>
/// <param name="Status">The outcome.</param>
/// <param name="ExitCode">The process exit code, or null when the tool did not run to completion.</param>
/// <param name="Reason">Why the tool is unavailable, timed out or failed. Null for <see cref="LuauToolStatus.Passed"/> and <see cref="LuauToolStatus.Findings"/>.</param>
/// <param name="OutputTruncated">True when the tool's output exceeded <see cref="LuauToolOptions.MaxOutputCharacters"/> and was cut.</param>
public sealed record LuauToolReport(
    LuauToolKind Tool,
    LuauToolStatus Status,
    int? ExitCode,
    string? Reason,
    bool OutputTruncated);
