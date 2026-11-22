namespace Bazaar.Server.Luau.Abstractions;

/// <summary>The headless Luau tools the adapter can run.</summary>
public enum LuauToolKind
{
    /// <summary><c>luau-analyze</c>: type checking and Luau lint diagnostics.</summary>
    Analyze,

    /// <summary><c>selene</c>: linting, read through its JSON-lines display style.</summary>
    Selene,

    /// <summary><c>stylua --check</c>: formatting check. Reports files that differ from StyLua's output.</summary>
    StyLuaCheck,
}

/// <summary>Outcome of one tool run.</summary>
public enum LuauToolStatus
{
    /// <summary>The tool exited cleanly and reported no diagnostics.</summary>
    Passed,

    /// <summary>The tool reported one or more diagnostics.</summary>
    Findings,

    /// <summary>The tool failed (non-zero exit with no parseable diagnostics). <see cref="LuauToolReport.Reason"/> says why.</summary>
    Error,

    /// <summary>The tool binary could not be started. <see cref="LuauToolReport.Reason"/> carries the cause.</summary>
    Unavailable,

    /// <summary>The tool exceeded its timeout and was killed. Its diagnostics are not reported.</summary>
    TimedOut,
}
