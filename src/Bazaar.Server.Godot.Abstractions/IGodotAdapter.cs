namespace Bazaar.Server.Godot.Abstractions;

/// <summary>
/// Runs Godot headlessly: build check, import, script check, test runner and export
/// (docs/design/20-engine-integration-api.md, Godot adapter). Runs are serialised per project directory.
/// </summary>
public interface IGodotAdapter
{
    /// <summary>
    /// Runs <c>--version</c> and checks editor build, variant, version pin and, when <paramref name="exportPreset"/> is given,
    /// the preset and its export templates. A missing binary is reported as <see cref="GodotBuildCheckStatus.Unavailable"/>.
    /// </summary>
    Task<GodotBuildCheckResult> BuildCheckAsync(string projectRoot, string? exportPreset = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs the startup canary (a script with a known error that must fail) and caches a pass. A canary that passes means
    /// Godot ignored the flags, so the adapter is unavailable.
    /// </summary>
    Task<GodotAvailability> EnsureAvailableAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs one command. Path or preset violations throw <see cref="GodotRequestRejectedException"/> before any process starts.
    /// An unavailable adapter, a timeout or a failing command is reported in the result. Caller cancellation throws
    /// <see cref="OperationCanceledException"/>.
    /// </summary>
    Task<GodotRunResult> RunAsync(GodotCommandRequest request, CancellationToken cancellationToken = default);
}
