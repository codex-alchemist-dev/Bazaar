namespace Bazaar.Server.Luau.Abstractions;

/// <summary>
/// Configurable inputs for the Luau tools. Binary paths default to bare names resolved through PATH. No tool version
/// is pinned: none has been verified for this adapter (docs/design/20-engine-integration-api.md, Q8).
/// </summary>
public sealed record LuauToolOptions
{
    /// <summary>Default output cap per tool stream, in characters (1 MiB of ASCII). Output beyond it is cut and marked.</summary>
    public const int DefaultMaxOutputCharacters = 1_048_576;

    /// <summary>Path to <c>luau-analyze</c>, or a bare name resolved through PATH.</summary>
    public string AnalyzeBinaryPath { get; init; } = "luau-analyze";

    /// <summary>Path to <c>selene</c>, or a bare name resolved through PATH.</summary>
    public string SeleneBinaryPath { get; init; } = "selene";

    /// <summary>Path to <c>stylua</c>, or a bare name resolved through PATH.</summary>
    public string StyLuaBinaryPath { get; init; } = "stylua";

    /// <summary>Wall-clock limit for each tool invocation when the request sets none.</summary>
    public TimeSpan DefaultTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Cap on each captured output stream, in characters. Output beyond it is dropped and the report is marked truncated.</summary>
    public int MaxOutputCharacters { get; init; } = DefaultMaxOutputCharacters;
}
