using Bazaar.Contracts.Coding;

namespace Bazaar.Server.Luau;

/// <summary>
/// One Luau check run, mapped for the harness. <see cref="Record"/> is the <c>check_runs</c> row. The row has no
/// pass/fail column, no per-tool status and no diagnostic text, so those travel beside it in this type.
/// </summary>
/// <param name="Record">The <c>check_runs</c> row for the run.</param>
/// <param name="Passed">True only when every requested tool ran, reported no Error diagnostic and its output was complete.</param>
/// <param name="Reasons">Why the run failed, one line per failing tool, in tool order. Empty when <paramref name="Passed"/> is true.</param>
/// <param name="Summary">Bounded, sanitised lines: one per requested tool (status and exit code), then diagnostics in a fixed order.</param>
public sealed record LuauCheckRunMapping(
    CheckRunRecord Record,
    bool Passed,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<string> Summary);
