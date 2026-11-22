namespace Bazaar.Server.Luau.Abstractions;

/// <summary>
/// Runs the headless Luau static checks (luau-analyze, Selene, StyLua) over files in a project root
/// (docs/design/20-engine-integration-api.md, Roblox adapter and Testing). No Roblox Studio is needed.
/// </summary>
public interface ILuauChecker
{
    /// <summary>
    /// Runs the requested tools over the requested files. A missing binary, a timeout or a tool error is reported
    /// per tool in <see cref="LuauCheckResult.Tools"/>; it does not stop the other tools. Path violations throw
    /// <see cref="LuauRequestRejectedException"/> before any tool runs. Caller cancellation throws
    /// <see cref="OperationCanceledException"/>.
    /// </summary>
    Task<LuauCheckResult> CheckAsync(LuauCheckRequest request, CancellationToken cancellationToken = default);
}
